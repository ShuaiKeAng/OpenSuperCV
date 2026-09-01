namespace SuperCV.Domain.AI;

public enum AiMessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
}

public sealed record AiMessage
{
    public AiMessage(
        AiMessageRole role,
        string content,
        IReadOnlyList<AiToolCall>? toolCalls = null,
        string? toolCallId = null,
        bool preserveWhenTrimming = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (toolCalls?.Any(call => call is null) == true)
        {
            throw new ArgumentException("AI tool calls cannot contain null values.", nameof(toolCalls));
        }

        if (role == AiMessageRole.Tool && string.IsNullOrWhiteSpace(toolCallId))
        {
            throw new ArgumentException("Tool messages require a tool call identifier.", nameof(toolCallId));
        }

        if (role != AiMessageRole.Tool && !string.IsNullOrEmpty(toolCallId))
        {
            throw new ArgumentException("Only tool messages may carry a tool call identifier.", nameof(toolCallId));
        }

        if (toolCalls is { Count: > 0 } && role != AiMessageRole.Assistant)
        {
            throw new ArgumentException("Only assistant messages may request tools.", nameof(toolCalls));
        }

        Role = role;
        Content = content;
        ToolCalls = toolCalls?.ToArray() ?? Array.Empty<AiToolCall>();
        ToolCallId = toolCallId;
        PreserveWhenTrimming = preserveWhenTrimming;
    }

    public AiMessageRole Role { get; init; }

    public string Content { get; init; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; init; }

    public string? ToolCallId { get; init; }

    /// <summary>
    /// Indicates that this message must be retained before ordinary conversational
    /// history when the global input hard cap is applied. It is local request metadata
    /// and is never sent to an AI provider.
    /// </summary>
    public bool PreserveWhenTrimming { get; init; }
}
