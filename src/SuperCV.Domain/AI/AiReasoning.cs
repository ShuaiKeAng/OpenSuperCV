namespace SuperCV.Domain.AI;

public enum AiReasoningEffort
{
    Off = 0,
    Low = 1,
    High = 2,
    Max = 3,
}

public enum AiCompletionChunkKind
{
    Content = 0,
    Reasoning = 1,
    Completion = 2,
}

public readonly record struct AiCompletionChunk(
    AiCompletionChunkKind Kind,
    string Text,
    AiTokenUsage? Usage = null,
    string? FinishReason = null);

public enum AiAgentStreamChunkKind
{
    Content = 0,
    Reasoning = 1,
    ToolCall = 2,
    Completion = 3,
}

public readonly record struct AiAgentStreamChunk(
    AiAgentStreamChunkKind Kind,
    string Text,
    int ToolCallIndex = -1,
    string? ToolCallId = null,
    string? ToolName = null,
    AiTokenUsage? Usage = null,
    string? FinishReason = null);
