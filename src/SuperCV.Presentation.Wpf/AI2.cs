using SuperCV.Application.Ports;
using SuperCV.Application.Settings;
using SuperCV.Domain.AI;
using System.Runtime.CompilerServices;
using DomainAiProvider = SuperCV.Domain.Settings.AiProvider;

namespace SuperCV;

public delegate void StreamOutputHandler(string chunk);
public delegate void StreamCompletionHandler(string content, string reasoning);

/// <summary>
/// UI-compatible adapter over the process-wide AI client. Construction is intentionally cheap:
/// no socket, handler, or <see cref="HttpClient"/> is created per request or per window.
/// </summary>
public sealed class AI2 : IDisposable
{
    private const int MaximumStreamingContentCharacters = 8 * 1024 * 1024;
    private readonly IAiClient _client;
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly string _modelName;
    private readonly string _basePrompt;
    private readonly DomainAiProvider _provider;

    public AI2(
        string? apiKey,
        AIProvider provider = AIProvider.DeepSeek,
        string? basePrompt = null,
        string? modelName = null,
        string? apiBaseUrl = null)
        : this(GetRuntime(), apiKey, provider, basePrompt, modelName, apiBaseUrl)
    {
    }

    internal AI2(
        AppRuntime runtime,
        string? apiKey,
        AIProvider provider = AIProvider.DeepSeek,
        string? basePrompt = null,
        string? modelName = null,
        string? apiBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _client = runtime.Ai;
        SettingsService settings = runtime.Settings;
        global::SuperCV.Domain.Settings.AppSettings snapshot = settings.Snapshot;

        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? settings.ApiKey
            : apiKey.Trim();
        _basePrompt = basePrompt ?? string.Empty;
        _provider = MapProvider(provider);

        bool useConfiguredValues = provider == MapProvider(snapshot.AiProvider)
                                   || provider is AIProvider.Custom or AIProvider.QianWen;
        string? configuredModel = useConfiguredValues ? snapshot.AiModelName : null;
        _modelName = string.IsNullOrWhiteSpace(modelName)
            ? AIProviderDefaults.GetConfiguredModelName(provider, configuredModel)
            : modelName.Trim();

        string configuredUrl = !string.IsNullOrWhiteSpace(apiBaseUrl)
            ? apiBaseUrl
            : useConfiguredValues
                ? snapshot.AiBaseUrl
                : AIProviderDefaults.GetDefaultApiUrl(provider);
        _apiUrl = AIProviderDefaults.GetRequestUrl(provider, configuredUrl);
    }

    public async Task AskStreamAsync(
        string instruction,
        string text = "",
        StreamOutputHandler? onCharReceived = null,
        StreamOutputHandler? onReasoningReceived = null,
        StreamCompletionHandler? onComplete = null,
        CancellationToken cancellationToken = default,
        AiReasoningEffort reasoningEffort = AiReasoningEffort.Off)
    {
        try
        {
            AiCompletionRequest request = CreateRequest(instruction, text, reasoningEffort);
            var completeContent = new System.Text.StringBuilder();
            var completeReasoning = new System.Text.StringBuilder();

            await foreach (AiCompletionChunk chunk in _client
                               .StreamDetailedAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (chunk.Kind == AiCompletionChunkKind.Completion)
                {
                    if (IsIncompleteFinishReason(chunk.FinishReason))
                    {
                        throw new InvalidOperationException(
                            $"AI 回答未完整生成（服务端结束原因：{chunk.FinishReason}）。");
                    }

                    continue;
                }

                if (chunk.Text.Length == 0)
                {
                    continue;
                }

                System.Text.StringBuilder target = chunk.Kind == AiCompletionChunkKind.Reasoning
                    ? completeReasoning
                    : completeContent;
                if (target.Length > MaximumStreamingContentCharacters - chunk.Text.Length)
                {
                    throw new InvalidOperationException("AI 流式响应超出允许的大小限制。");
                }

                target.Append(chunk.Text);
                if (chunk.Kind == AiCompletionChunkKind.Reasoning)
                {
                    onReasoningReceived?.Invoke(chunk.Text);
                }
                else
                {
                    onCharReceived?.Invoke(chunk.Text);
                }
            }

            onComplete?.Invoke(completeContent.ToString(), completeReasoning.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"AI流式请求失败: {exception.Message}", exception);
        }
    }

    private static bool IsIncompleteFinishReason(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(finishReason, "max_completion_tokens", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase);

    public async Task<string> AskAsync(
        string instruction,
        string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client
                .CompleteAsync(CreateRequest(instruction, text, AiReasoningEffort.Off), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"AI请求失败: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Executes a reusable text transformation. Unlike a general AI request, the source is
    /// serialized into a data-only envelope so that the transformation prompt can reliably
    /// distinguish it from executable instructions.
    /// </summary>
    public async Task<string> TransformTextAsync(
        string task,
        string sourceText,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client
                .CompleteAsync(
                    CreateTextTransformationRequest(task, sourceText, AiReasoningEffort.Off),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"AI文本转换请求失败: {exception.Message}", exception);
        }
    }

    public async Task<AiCompletionResult> AskWithToolsAsync(
        string instruction,
        string text,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
        {
            throw new ArgumentException("At least one AI tool is required.", nameof(tools));
        }

        try
        {
            return await _client
                .CompleteDetailedAsync(
                    CreateRequest(
                        instruction,
                        text,
                        AiReasoningEffort.Off,
                        tools),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"AI工具调用失败: {exception.Message}", exception);
        }
    }

    public async Task<AiCompletionResult> CompleteAgentTurnAsync(
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        AiReasoningEffort reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        if (messages.Count == 0)
        {
            throw new ArgumentException("Agent messages cannot be empty.", nameof(messages));
        }

        if (tools.Count == 0)
        {
            throw new ArgumentException("Agent tools cannot be empty.", nameof(tools));
        }

        try
        {
            return await _client.CompleteDetailedAsync(
                    CreateAgentRequest(messages, tools, reasoningEffort),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Agent 请求失败: {exception.Message}", exception);
        }
    }

    public async IAsyncEnumerable<AiAgentStreamChunk> StreamAgentTurnAsync(
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        AiReasoningEffort reasoningEffort,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        if (messages.Count == 0)
        {
            throw new ArgumentException("Agent messages cannot be empty.", nameof(messages));
        }

        if (tools.Count == 0)
        {
            throw new ArgumentException("Agent tools cannot be empty.", nameof(tools));
        }

        AiCompletionRequest request = CreateAgentRequest(messages, tools, reasoningEffort);
        IAsyncEnumerable<AiAgentStreamChunk> stream;
        try
        {
            stream = _client.StreamAgentAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Agent 流式请求失败: {exception.Message}", exception);
        }

        await foreach (AiAgentStreamChunk chunk in stream
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private AiCompletionRequest CreateAgentRequest(
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        AiReasoningEffort reasoningEffort)
    {
        EnsureConfiguration();
        AiMessage[] preparedMessages = messages.ToArray();
        if (!string.IsNullOrWhiteSpace(_basePrompt))
        {
            int systemIndex = Array.FindIndex(
                preparedMessages,
                message => message.Role == AiMessageRole.System);
            if (systemIndex >= 0)
            {
                AiMessage system = preparedMessages[systemIndex];
                preparedMessages[systemIndex] = system with
                {
                    Content = string.Concat(_basePrompt, "\n\n", system.Content),
                };
            }
            else
            {
                preparedMessages =
                [
                    new AiMessage(AiMessageRole.System, _basePrompt),
                    .. preparedMessages,
                ];
            }
        }

        return new AiCompletionRequest(
            _provider,
            _apiKey,
            _apiUrl,
            _modelName,
            preparedMessages,
            temperature: 0.7,
            reasoningEffort: reasoningEffort,
            tools: tools,
            toolChoice: AiToolChoice.Auto);
    }

    private AiCompletionRequest CreateRequest(
        string? instruction,
        string? text,
        AiReasoningEffort reasoningEffort,
        IReadOnlyList<AiToolDefinition>? tools = null)
    {
        EnsureConfiguration();
        return new AiCompletionRequest(
            _provider,
            _apiKey,
            _apiUrl,
            _modelName,
            new[]
            {
                new AiMessage(
                    AiMessageRole.System,
                    string.Concat(_basePrompt, instruction ?? string.Empty)),
                new AiMessage(AiMessageRole.User, text ?? string.Empty),
            },
            temperature: 0.7,
            reasoningEffort: reasoningEffort,
            tools: tools,
            toolChoice: AiToolChoice.Auto);
    }

    private AiCompletionRequest CreateTextTransformationRequest(
        string? task,
        string? sourceText,
        AiReasoningEffort reasoningEffort)
    {
        EnsureConfiguration();
        string systemPrompt = string.Concat(
            _basePrompt,
            "\n\n<TRANSFORMATION_TASK>\n",
            task ?? string.Empty,
            "\n</TRANSFORMATION_TASK>");
        string sourceEnvelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            source_text = sourceText ?? string.Empty,
        });

        return new AiCompletionRequest(
            _provider,
            _apiKey,
            _apiUrl,
            _modelName,
            new[]
            {
                new AiMessage(AiMessageRole.System, systemPrompt),
                new AiMessage(AiMessageRole.User, sourceEnvelope),
            },
            temperature: 0.7,
            reasoningEffort: reasoningEffort);
    }

    private void EnsureConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_apiKey) &&
            !AIProviderDefaults.AllowsEmptyApiKey(_apiUrl))
        {
            throw new InvalidOperationException("未设置 API Key。远程服务必须设置密钥；本机回环服务可留空。");
        }

        if (string.IsNullOrWhiteSpace(_apiUrl))
        {
            throw new InvalidOperationException("未设置有效的 API 地址。");
        }

        if (string.IsNullOrWhiteSpace(_modelName))
        {
            throw new InvalidOperationException("未设置有效的模型名称。");
        }
    }

    private static AppRuntime GetRuntime()
    {
        if (System.Windows.Application.Current is not App { Runtime: { } runtime })
        {
            throw new InvalidOperationException("SuperCV 运行时尚未初始化。");
        }

        return runtime;
    }

    private static DomainAiProvider MapProvider(AIProvider provider) => provider switch
    {
        AIProvider.DeepSeek => DomainAiProvider.DeepSeek,
        AIProvider.ChatGLM => DomainAiProvider.ChatGlm,
        AIProvider.QianWen => DomainAiProvider.QianWen,
        AIProvider.Minimax => DomainAiProvider.Minimax,
        AIProvider.Custom => DomainAiProvider.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的 AI 服务商。"),
    };

    private static AIProvider MapProvider(DomainAiProvider provider) => provider switch
    {
        DomainAiProvider.DeepSeek => AIProvider.DeepSeek,
        DomainAiProvider.ChatGlm => AIProvider.ChatGLM,
        DomainAiProvider.QianWen => AIProvider.QianWen,
        DomainAiProvider.Minimax => AIProvider.Minimax,
        DomainAiProvider.Custom => AIProvider.Custom,
        _ => AIProvider.DeepSeek,
    };

    // Retained so existing using-statements remain source-compatible. The shared runtime owns the client.
    public void Dispose()
    {
    }
}
