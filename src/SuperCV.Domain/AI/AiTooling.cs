using System.Text.Json;

namespace SuperCV.Domain.AI;

public enum AiToolChoice
{
    Auto = 0,
    None = 1,
    Required = 2,
}

public sealed class AiToolDefinition
{
    public AiToolDefinition(
        string name,
        string description,
        string parametersJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJson);
        if (name.Length > 64 || name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException(
                "AI tool names must contain only ASCII letters, digits, underscores, or hyphens and be at most 64 characters.",
                nameof(name));
        }

        using JsonDocument document = JsonDocument.Parse(parametersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("AI tool parameters must be a JSON Schema object.", nameof(parametersJson));
        }

        Name = name;
        Description = description;
        Parameters = document.RootElement.Clone();
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement Parameters { get; }
}

public sealed record AiToolCall(string Id, string Name, string Arguments);

public sealed class AiCompletionResult
{
    public AiCompletionResult(
        string content,
        IReadOnlyList<AiToolCall> toolCalls,
        string reasoningContent = "",
        AiTokenUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentNullException.ThrowIfNull(reasoningContent);
        Content = content;
        ToolCalls = toolCalls.ToArray();
        ReasoningContent = reasoningContent;
        Usage = usage;
    }

    public string Content { get; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; }

    public string ReasoningContent { get; }

    /// <summary>Exact provider-reported usage, or <see langword="null"/> when the API omitted it.</summary>
    public AiTokenUsage? Usage { get; }
}
