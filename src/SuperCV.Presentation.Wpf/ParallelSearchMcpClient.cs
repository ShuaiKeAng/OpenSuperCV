using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SuperCV;

/// <summary>
/// Small, deliberately narrow Streamable HTTP MCP harness for Parallel's anonymous search.
/// It exposes no URL fetch capability and creates a fresh short-lived MCP session per search.
/// </summary>
internal sealed class ParallelSearchMcpClient
{
    private const string Endpoint = "https://search.parallel.ai/mcp";
    private const string ProtocolVersion = "2025-06-18";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    internal async Task<string> SearchAsync(
        string objective,
        IReadOnlyList<string> searchQueries,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken requestToken = timeout.Token;

        using McpResponse initialized = await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "SuperCV", version = "1.0" },
            },
        }, null, includeProtocolVersion: false, requestToken);
        ThrowIfError(initialized.Payload);

        if (!string.IsNullOrEmpty(initialized.SessionId))
        {
            using McpResponse _ = await SendAsync(new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            }, initialized.SessionId, includeProtocolVersion: true, requestToken);
        }

        using McpResponse response = await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "web_search",
                arguments = new
                {
                    objective,
                    search_queries = searchQueries,
                    session_id = sessionId,
                },
            },
        }, initialized.SessionId, includeProtocolVersion: true, requestToken);
        ThrowIfError(response.Payload);
        return ExtractText(response.Payload);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task<McpResponse> SendAsync(
        object payload,
        string? sessionId,
        bool includeProtocolVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (includeProtocolVersion)
        {
            request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Parallel Search 服务返回 {(int)response.StatusCode} ({response.ReasonPhrase})。",
                null,
                response.StatusCode);
        }

        return new McpResponse(
            string.IsNullOrWhiteSpace(body) ? JsonDocument.Parse("{}") : ParseMcpPayload(body),
            response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null);
    }

    private static JsonDocument ParseMcpPayload(string body)
    {
        string json = body.Trim();
        if (json.Contains("data:", StringComparison.Ordinal))
        {
            json = string.Join(
                Environment.NewLine,
                json.Split('\n')
                    .Select(line => line.TrimStart().StartsWith("data:", StringComparison.Ordinal)
                        ? line.TrimStart()["data:".Length..].TrimStart()
                        : string.Empty)
                    .Where(line => line.Length > 0));
        }

        return JsonDocument.Parse(json);
    }

    private static void ThrowIfError(JsonDocument payload)
    {
        JsonElement root = payload.RootElement;
        if (!root.TryGetProperty("error", out JsonElement error))
        {
            return;
        }

        string message = error.TryGetProperty("message", out JsonElement value)
            ? value.GetString() ?? "未知 MCP 错误。"
            : "未知 MCP 错误。";
        throw new InvalidOperationException($"Parallel Search MCP 错误：{message}");
    }

    private static string ExtractText(JsonDocument payload)
    {
        JsonElement root = payload.RootElement;
        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Parallel Search MCP 返回了无法识别的工具结果。 ");
        }

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean())
        {
            throw new InvalidOperationException($"Parallel Search MCP 错误：{ExtractContentText(content)}");
        }

        string text = ExtractContentText(content);
        return text.Length > 0
            ? text
            : throw new InvalidOperationException("Parallel Search MCP 未返回可用的文字搜索结果。 ");
    }

    private static string ExtractContentText(JsonElement content) => string.Join(
            Environment.NewLine,
            content.EnumerateArray()
                .Where(item => item.TryGetProperty("type", out JsonElement type) &&
                    string.Equals(type.GetString(), "text", StringComparison.Ordinal) &&
                    item.TryGetProperty("text", out JsonElement value))
                .Select(item => item.GetProperty("text").GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private sealed record McpResponse(JsonDocument Payload, string? SessionId) : IDisposable
    {
        public void Dispose() => Payload.Dispose();
    }
}
