using SuperCV.Application.Ports;
using SuperCV.Domain.AI;
using System.Runtime.CompilerServices;

namespace SuperCV.Infrastructure.Windows;

public sealed class OpenAiCompatibleClient : IAiClient
{
    private readonly OpenAiCompatibleTransport _transport;
    private readonly AiTokenUsageLedger? _usageLedger;
    private Func<int>? _maximumContextTokensProvider;

    public OpenAiCompatibleClient()
        : this(new OpenAiCompatibleTransport())
    {
    }

    public OpenAiCompatibleClient(TimeSpan requestTimeout)
        : this(new OpenAiCompatibleTransport(requestTimeout: requestTimeout))
    {
    }

    public OpenAiCompatibleClient(
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        AiTokenUsageLedger? usageLedger = null,
        Func<int>? maximumContextTokensProvider = null)
        : this(new OpenAiCompatibleTransport(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            requestTimeout), usageLedger, maximumContextTokensProvider)
    {
    }

    public OpenAiCompatibleClient(AiTokenUsageLedger usageLedger)
        : this(new OpenAiCompatibleTransport(), usageLedger)
    {
    }

    public OpenAiCompatibleClient(TimeSpan requestTimeout, AiTokenUsageLedger usageLedger)
        : this(new OpenAiCompatibleTransport(requestTimeout: requestTimeout), usageLedger)
    {
    }

    internal OpenAiCompatibleClient(
        OpenAiCompatibleTransport transport,
        AiTokenUsageLedger? usageLedger = null,
        Func<int>? maximumContextTokensProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _usageLedger = usageLedger;
        _maximumContextTokensProvider = maximumContextTokensProvider;
    }

    /// <summary>
    /// Configures the process-wide settings source consulted immediately before each model request.
    /// </summary>
    public void SetMaximumContextTokensProvider(Func<int> maximumContextTokensProvider)
    {
        ArgumentNullException.ThrowIfNull(maximumContextTokensProvider);
        Volatile.Write(ref _maximumContextTokensProvider, maximumContextTokensProvider);
    }

    public async Task<string> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        AiCompletionResult result = await CompleteDetailedAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (result.ToolCalls.Count > 0 && string.IsNullOrEmpty(result.Content))
        {
            throw new OpenAiProtocolException(
                "The AI returned a tool call to a text-only completion request.");
        }

        return result.Content;
    }

    public async Task<AiCompletionResult> CompleteDetailedAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiCompletionRequest limitedRequest = ApplyContextLimit(request);
        AiCompletionResult result = await _transport.CompleteDetailedAsync(
            ResolveChatCompletionsEndpoint(limitedRequest.ApiUrl),
            limitedRequest.Model,
            limitedRequest.ApiKey,
            MapMessages(limitedRequest.Messages),
            limitedRequest.Temperature,
            limitedRequest.Provider,
            limitedRequest.ReasoningEffort,
            limitedRequest.Tools,
            limitedRequest.ToolChoice,
            cancellationToken).ConfigureAwait(false);
        await RecordUsageAsync(limitedRequest, result.Usage).ConfigureAwait(false);
        return result;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AiCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureStreamingRequestHasNoTools(request);
        await foreach (AiCompletionChunk chunk in StreamDetailedAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (chunk.Kind == AiCompletionChunkKind.Completion &&
                IsIncompleteFinishReason(chunk.FinishReason))
            {
                throw new OpenAiProtocolException(
                    $"The streaming AI response ended before completion ({chunk.FinishReason}).");
            }

            if (chunk.Kind == AiCompletionChunkKind.Content && chunk.Text.Length > 0)
            {
                yield return chunk.Text;
            }
        }
    }

    public async IAsyncEnumerable<AiCompletionChunk> StreamDetailedAsync(
        AiCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureStreamingRequestHasNoTools(request);
        AiCompletionRequest limitedRequest = ApplyContextLimit(request);
        await foreach (AiCompletionChunk chunk in _transport.StreamDetailedAsync(
            ResolveChatCompletionsEndpoint(limitedRequest.ApiUrl),
            limitedRequest.Model,
            limitedRequest.ApiKey,
            MapMessages(limitedRequest.Messages),
            limitedRequest.Temperature,
            limitedRequest.Provider,
            limitedRequest.ReasoningEffort,
            cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Usage is not null)
            {
                await RecordUsageAsync(limitedRequest, chunk.Usage).ConfigureAwait(false);
            }

            yield return chunk;
        }
    }

    public async IAsyncEnumerable<AiAgentStreamChunk> StreamAgentAsync(
        AiCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools.Count == 0)
        {
            throw new ArgumentException(
                "Agent streaming requests require at least one tool.",
                nameof(request));
        }

        AiCompletionRequest limitedRequest = ApplyContextLimit(request);
        await foreach (AiAgentStreamChunk chunk in _transport.StreamAgentAsync(
            ResolveChatCompletionsEndpoint(limitedRequest.ApiUrl),
            limitedRequest.Model,
            limitedRequest.ApiKey,
            MapMessages(limitedRequest.Messages),
            limitedRequest.Temperature,
            limitedRequest.Provider,
            limitedRequest.ReasoningEffort,
            limitedRequest.Tools,
            limitedRequest.ToolChoice,
            cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Usage is not null)
            {
                await RecordUsageAsync(limitedRequest, chunk.Usage).ConfigureAwait(false);
            }

            yield return chunk;
        }
    }

    private static bool IsIncompleteFinishReason(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(finishReason, "max_completion_tokens", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase);

    private AiCompletionRequest ApplyContextLimit(AiCompletionRequest request)
    {
        Func<int>? provider = Volatile.Read(ref _maximumContextTokensProvider);
        return provider is null ? request : AiContextTokenLimiter.Limit(request, provider());
    }

    private static IReadOnlyList<OpenAiWireMessage> MapMessages(IReadOnlyList<AiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var mapped = new List<OpenAiWireMessage>(messages.Count);
        foreach (AiMessage message in messages)
        {
            if (message is null)
            {
                throw new ArgumentException("AI messages cannot contain null values.", nameof(messages));
            }

            string role = message.Role switch
            {
                AiMessageRole.System => "system",
                AiMessageRole.User => "user",
                AiMessageRole.Assistant => "assistant",
                AiMessageRole.Tool => "tool",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(messages),
                    message.Role,
                    "The AI message role is unsupported."),
            };

            mapped.Add(new OpenAiWireMessage(
                role,
                message.Content,
                message.ToolCalls,
                message.ToolCallId));
        }

        return mapped;
    }

    private async Task RecordUsageAsync(AiCompletionRequest request, AiTokenUsage? usage)
    {
        if (_usageLedger is null || usage is null)
        {
            return;
        }

        try
        {
            await _usageLedger.RecordAsync(request.Provider, request.Model, usage).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Usage persistence is observability only and must never invalidate a completed request.
        }
    }

    private static void EnsureStreamingRequestHasNoTools(AiCompletionRequest request)
    {
        if (request.Tools.Count > 0)
        {
            throw new NotSupportedException(
                "Streaming tool calls are not supported by this AI client. Use CompleteDetailedAsync instead.");
        }
    }

    private static Uri ResolveChatCompletionsEndpoint(string configuredUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredUrl);
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out Uri? configuredUri) ||
            (configuredUri.Scheme != Uri.UriSchemeHttps &&
             !(configuredUri.Scheme == Uri.UriSchemeHttp && configuredUri.IsLoopback)))
        {
            throw new ArgumentException(
                "The configured AI URL must use HTTPS; HTTP is allowed only for loopback development endpoints.",
                nameof(configuredUrl));
        }

        string path = configuredUri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path += "/chat/completions";
        }

        var builder = new UriBuilder(configuredUri)
        {
            Fragment = string.Empty,
            Path = path,
        };
        return builder.Uri;
    }
}
