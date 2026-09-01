using System.Text;

namespace SuperCV.Domain.AI;

/// <summary>
/// Indicates that a protected in-progress Agent turn cannot fit in the configured
/// absolute context limit without dropping or truncating part of that turn.
/// </summary>
public sealed class AiProtectedContextLimitExceededException : InvalidOperationException
{
    public AiProtectedContextLimitExceededException()
        : base(
            "当前 Agent 对话执行链已超过最大上下文长度；为避免丢失本轮工具结果，已停止后续模型调用。请提高上下文档位或缩小本次任务范围。 ")
    {
    }
}

/// <summary>
/// Applies a conservative, provider-independent input budget before a request reaches an AI model.
/// The estimate uses UTF-8 bytes as an upper bound for text tokens and reserves protocol overhead,
/// so it deliberately favors a hard limit over maximizing the amount of text sent.
/// </summary>
public static class AiContextTokenLimiter
{
    private const int RequestOverheadTokens = 128;
    private const int MessageOverheadTokens = 16;
    private const int ToolDefinitionOverheadTokens = 32;
    private const int ToolCallOverheadTokens = 24;

    /// <summary>
    /// Returns the conservative upper-bound input size used by <see cref="Limit"/>.
    /// </summary>
    public static int EstimateUpperBoundTokens(AiCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EstimateUpperBoundTokens(request.Messages, request.Tools);
    }

    /// <summary>
    /// Returns the conservative upper-bound input size for messages and optional tool definitions.
    /// </summary>
    public static int EstimateUpperBoundTokens(
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long total = RequestOverheadTokens;
        total += (tools ?? Array.Empty<AiToolDefinition>())
            .Select(tool => (long)EstimateToolDefinitionTokens(tool))
            .Sum();
        total += messages.Select(message => (long)EstimateMessageTokens(message)).Sum();
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// Truncates plain text to the conservative provider-independent upper bound used for context.
    /// </summary>
    public static string TruncateTextToUpperBoundTokens(string text, int maximumTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        return TruncateToUtf8Bytes(text, maximumTokens);
    }

    /// <summary>
    /// Drops older non-system messages first and truncates the remaining message text when needed.
    /// No summary or semantic compression is performed.
    /// </summary>
    public static AiCompletionRequest Limit(AiCompletionRequest request, int maximumTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        if (EstimateUpperBoundTokens(request) <= maximumTokens)
        {
            return request;
        }

        EnsurePreservedMessagesFit(request, maximumTokens);

        long remaining = maximumTokens - RequestOverheadTokens - request.Tools
            .Select(tool => (long)EstimateToolDefinitionTokens(tool))
            .Sum();
        if (remaining <= 0)
        {
            throw new InvalidOperationException(
                "AI tool definitions exceed the configured maximum context length.");
        }

        var retained = new AiMessage?[request.Messages.Count];

        // A caller may mark the complete in-progress agent turn as fidelity anchors.
        // Preserve every marked message before other input: otherwise a just-returned
        // tool result could be trimmed before the next agent step reads it, causing a
        // repeated tool-call loop. The messages retain their original roles, so this
        // does not elevate untrusted content into a system instruction.
        foreach (int index in Enumerable.Range(0, request.Messages.Count)
                     .Where(index =>
                         request.Messages[index].PreserveWhenTrimming &&
                         request.Messages[index].Role != AiMessageRole.System))
        {
            RetainMessage(request.Messages[index], index, retained, ref remaining);
            if (remaining <= 0)
            {
                return CreateLimitedRequest(request, retained);
            }
        }

        // System instructions establish the contract for every request, so retain them before
        // ordinary conversational history. Other messages are then considered from newest to oldest.
        foreach (int index in Enumerable.Range(0, request.Messages.Count)
                     .Where(index => request.Messages[index].Role == AiMessageRole.System))
        {
            RetainMessage(request.Messages[index], index, retained, ref remaining);
            if (remaining <= 0)
            {
                return CreateLimitedRequest(request, retained);
            }
        }

        for (int index = request.Messages.Count - 1; index >= 0 && remaining > 0; index--)
        {
            if (request.Messages[index].Role == AiMessageRole.System || retained[index] is not null)
            {
                continue;
            }

            RetainMessage(request.Messages[index], index, retained, ref remaining);
        }

        return CreateLimitedRequest(request, retained);
    }

    private static void RetainMessage(
        AiMessage message,
        int index,
        AiMessage?[] retained,
        ref long remaining)
    {
        int metadataTokens = EstimateMessageMetadataTokens(message);
        if (metadataTokens > remaining)
        {
            return;
        }

        remaining -= metadataTokens;
        string content = TruncateToUtf8Bytes(message.Content, remaining);
        remaining -= Utf8ByteCount(content);
        retained[index] = content.Length == message.Content.Length
            ? message
            : message with { Content = content };
    }

    private static AiCompletionRequest CreateLimitedRequest(
        AiCompletionRequest request,
        IReadOnlyList<AiMessage?> retained)
    {
        AiMessage[] messages = retained.Where(message => message is not null)
            .Select(message => message!)
            .ToArray();
        var retainedToolCallIds = messages
            .SelectMany(message => message.ToolCalls)
            .Select(call => call.Id)
            .ToHashSet(StringComparer.Ordinal);
        messages = messages
            .Where(message =>
                message.Role != AiMessageRole.Tool ||
                retainedToolCallIds.Contains(message.ToolCallId!))
            .ToArray();
        if (messages.Length == 0)
        {
            throw new InvalidOperationException(
                "The configured maximum context length is too small to send an AI message.");
        }

        return new AiCompletionRequest(
            request.Provider,
            request.ApiKey,
            request.ApiUrl,
            request.Model,
            messages,
            request.Temperature,
            request.ReasoningEffort,
            request.Tools,
            request.ToolChoice);
    }

    private static void EnsurePreservedMessagesFit(
        AiCompletionRequest request,
        int maximumTokens)
    {
        AiMessage[] preservedMessages = request.Messages
            .Where(message =>
                message.PreserveWhenTrimming &&
                message.Role != AiMessageRole.System)
            .ToArray();
        if (preservedMessages.Length == 0)
        {
            return;
        }

        // Do not silently truncate any part of the active Agent turn. System messages
        // remain eligible for ordinary hard-cap handling, but the request envelope,
        // tool definitions, and every protected turn message must fit as-is.
        int preservedTokens = EstimateUpperBoundTokens(preservedMessages, request.Tools);
        if (preservedTokens > maximumTokens)
        {
            throw new AiProtectedContextLimitExceededException();
        }
    }

    private static int EstimateMessageTokens(AiMessage message) =>
        EstimateMessageMetadataTokens(message) + Utf8ByteCount(message.Content);

    private static int EstimateMessageMetadataTokens(AiMessage message)
    {
        long total = MessageOverheadTokens + Utf8ByteCount(message.ToolCallId);
        foreach (AiToolCall call in message.ToolCalls)
        {
            total += ToolCallOverheadTokens;
            total += Utf8ByteCount(call.Id);
            total += Utf8ByteCount(call.Name);
            total += Utf8ByteCount(call.Arguments);
        }

        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    private static int EstimateToolDefinitionTokens(AiToolDefinition tool)
    {
        long total = ToolDefinitionOverheadTokens;
        total += Utf8ByteCount(tool.Name);
        total += Utf8ByteCount(tool.Description);
        total += Utf8ByteCount(tool.Parameters.GetRawText());
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    private static string TruncateToUtf8Bytes(string content, long maximumBytes)
    {
        if (maximumBytes <= 0 || content.Length == 0)
        {
            return string.Empty;
        }

        if (Utf8ByteCount(content) <= maximumBytes)
        {
            return content;
        }

        int low = 0;
        int high = content.Length;
        while (low < high)
        {
            int candidate = low + ((high - low + 1) / 2);
            if (candidate < content.Length &&
                char.IsHighSurrogate(content[candidate - 1]) &&
                char.IsLowSurrogate(content[candidate]))
            {
                candidate++;
                if (candidate > high)
                {
                    candidate -= 2;
                }
            }

            if (candidate <= low)
            {
                high = low;
                continue;
            }

            if (Utf8ByteCount(content.AsSpan(0, candidate)) <= maximumBytes)
            {
                low = candidate;
            }
            else
            {
                high = candidate - 1;
            }
        }

        return content[..low];
    }

    private static int Utf8ByteCount(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private static int Utf8ByteCount(ReadOnlySpan<char> value) => Encoding.UTF8.GetByteCount(value);
}
