using SuperCV.Domain.Settings;

namespace SuperCV.Domain.AI;

public sealed class AiCompletionRequest
{
    public AiCompletionRequest(
        AiProvider provider,
        string apiKey,
        string apiUrl,
        string model,
        IReadOnlyList<AiMessage> messages,
        double temperature = 0.7,
        AiReasoningEffort reasoningEffort = AiReasoningEffort.Off,
        IReadOnlyList<AiToolDefinition>? tools = null,
        AiToolChoice toolChoice = AiToolChoice.Auto)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        if (tools?.Any(tool => tool is null) == true)
        {
            throw new ArgumentException("AI tools cannot contain null values.", nameof(tools));
        }

        Provider = provider;
        ApiKey = apiKey;
        ApiUrl = apiUrl.Trim();
        Model = model.Trim();
        Messages = messages.ToArray();
        Temperature = Math.Clamp(temperature, 0.0, 2.0);
        ReasoningEffort = Enum.IsDefined(reasoningEffort)
            ? reasoningEffort
            : AiReasoningEffort.Off;
        Tools = tools?.ToArray() ?? Array.Empty<AiToolDefinition>();
        ToolChoice = Enum.IsDefined(toolChoice) ? toolChoice : AiToolChoice.Auto;
    }

    public AiProvider Provider { get; }

    public string ApiKey { get; }

    public string ApiUrl { get; }

    public string Model { get; }

    public IReadOnlyList<AiMessage> Messages { get; }

    public double Temperature { get; }

    public AiReasoningEffort ReasoningEffort { get; }

    public IReadOnlyList<AiToolDefinition> Tools { get; }

    public AiToolChoice ToolChoice { get; }

    public override string ToString() =>
        $"{nameof(AiCompletionRequest)} {{ Provider = {Provider}, ApiUrl = {ApiUrl}, " +
        $"Model = {Model}, Messages = {Messages.Count}, Tools = {Tools.Count}, Temperature = {Temperature}, " +
        $"ReasoningEffort = {ReasoningEffort} }}";
}
