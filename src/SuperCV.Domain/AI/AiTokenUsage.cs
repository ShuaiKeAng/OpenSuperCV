namespace SuperCV.Domain.AI;

/// <summary>
/// Token counts returned by the model provider in its OpenAI-compatible <c>usage</c> payload.
/// Values are never inferred from local text.
/// </summary>
public sealed record AiTokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int ReasoningTokens = 0,
    int CachedTokens = 0)
{
    public bool IsValid => PromptTokens >= 0 && CompletionTokens >= 0 && TotalTokens >= 0 &&
                           ReasoningTokens >= 0 && CachedTokens >= 0;
}
