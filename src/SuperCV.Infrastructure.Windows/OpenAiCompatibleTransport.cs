using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperCV.Domain.AI;
using SuperCV.Domain.Settings;

namespace SuperCV.Infrastructure.Windows;

internal sealed record OpenAiWireMessage(
    string Role,
    string Content,
    IReadOnlyList<AiToolCall> ToolCalls,
    string? ToolCallId);

internal sealed class OpenAiCompatibleTransport
{
    private const int MaximumJsonResponseBytes = 8 * 1024 * 1024;
    private const int MaximumStreamingContentCharacters = 8 * 1024 * 1024;
    private const int ReadBufferSize = 16 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions WireJsonOptions = CreateWireJsonOptions();

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    internal OpenAiCompatibleTransport(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? SharedOpenAiHttpClient.Instance;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

        if (_requestTimeout <= TimeSpan.Zero && _requestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    internal async Task<AiCompletionResult> CompleteDetailedAsync(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        IReadOnlyList<AiToolDefinition> tools,
        AiToolChoice toolChoice,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeoutSource(cancellationToken);

        try
        {
            using HttpResponseMessage response = await SendWithReasoningFallbackAsync(
                    endpoint,
                    model,
                    apiKey,
                    messages,
                    temperature,
                    provider,
                    reasoningEffort,
                    tools,
                    toolChoice,
                    stream: false,
                    timeoutSource.Token)
                .ConfigureAwait(false);

            await EnsureSuccessAsync(response, timeoutSource.Token).ConfigureAwait(false);
            byte[] responseBytes = await ReadBoundedContentAsync(
                    response.Content,
                    MaximumJsonResponseBytes,
                    timeoutSource.Token)
                .ConfigureAwait(false);

            try
            {
                WireCompletionResponse? completion = JsonSerializer.Deserialize<WireCompletionResponse>(
                    responseBytes,
                    WireJsonOptions);

                if (completion?.Choices is null || completion.Choices.Count == 0)
                {
                    throw new OpenAiProtocolException("The AI response did not contain a completion choice.");
                }

                var result = new StringBuilder();
                var reasoning = new StringBuilder();
                var toolCalls = new List<AiToolCall>();
                bool sawResult = false;
                foreach (WireChoice choice in completion.Choices)
                {
                    if (choice.Message is not null)
                    {
                        sawResult |= choice.Message.Content.ValueKind is not (
                            JsonValueKind.Undefined or JsonValueKind.Null);
                        AppendContent(result, choice.Message.Content);
                        AppendContent(reasoning, choice.Message.ReasoningContent);
                        AppendReasoningDetails(reasoning, choice.Message.ReasoningDetails);
                        if (choice.Message.ToolCalls is not null)
                        {
                            foreach (WireToolCall toolCall in choice.Message.ToolCalls)
                            {
                                toolCalls.Add(ParseToolCall(toolCall));
                                sawResult = true;
                            }
                        }

                        if (choice.Message.FunctionCall is not null)
                        {
                            toolCalls.Add(ParseLegacyFunctionCall(choice.Message.FunctionCall));
                            sawResult = true;
                        }
                    }
                }

                if (!sawResult)
                {
                    throw new OpenAiProtocolException(
                        "The AI response did not contain text content or a tool call.");
                }

                return new AiCompletionResult(
                    result.ToString(),
                    toolCalls,
                    reasoning.ToString(),
                    MapUsage(completion.Usage));
            }
            catch (JsonException exception)
            {
                throw new OpenAiProtocolException("The AI response was not valid completion JSON.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBytes);
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("The AI request timed out.", exception);
        }
    }

    internal async IAsyncEnumerable<string> StreamAsync(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (AiCompletionChunk chunk in StreamDetailedAsync(
                           endpoint,
                           model,
                           apiKey,
                           messages,
                           temperature,
                           provider,
                           reasoningEffort,
                           cancellationToken)
                           .ConfigureAwait(false))
        {
            if (chunk.Kind == AiCompletionChunkKind.Content)
            {
                yield return chunk.Text;
            }
        }
    }

    internal async IAsyncEnumerable<AiCompletionChunk> StreamDetailedAsync(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        using HttpResponseMessage response = await SendWithReasoningFallbackAsync(
                endpoint,
                model,
                apiKey,
                messages,
                temperature,
                provider,
                reasoningEffort,
                tools: null,
                toolChoice: AiToolChoice.Auto,
                stream: true,
                timeoutSource.Token)
            .ConfigureAwait(false);

        await EnsureSuccessWithTimeoutAsync(response, timeoutSource, cancellationToken).ConfigureAwait(false);

        await using Stream responseStream = await ReadResponseStreamWithTimeoutAsync(
                response.Content,
                timeoutSource,
                cancellationToken)
            .ConfigureAwait(false);

        IAsyncEnumerator<SseEvent> events = SseParser
            .ReadEventsAsync(responseStream, cancellationToken: timeoutSource.Token)
            .GetAsyncEnumerator(timeoutSource.Token);

        bool sawTerminalSignal = false;
        string? finishReason = null;
        int streamedContentCharacters = 0;
        int streamedReasoningCharacters = 0;
        ThinkTagStreamParser? thinkTagParser = provider == AiProvider.Custom
            ? new ThinkTagStreamParser()
            : null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                {
                    throw new TimeoutException("The streaming AI request timed out.", exception);
                }

                if (!hasNext)
                {
                    break;
                }

                SseEvent serverEvent = events.Current;
                string eventData = serverEvent.Data;
                if (eventData.AsSpan().Trim().SequenceEqual("[DONE]"))
                {
                    sawTerminalSignal = true;
                    break;
                }

                if (string.Equals(serverEvent.EventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OpenAiProtocolException("The AI provider returned an SSE error event.");
                }

                WireStreamResponse streamResponse;
                try
                {
                    streamResponse = JsonSerializer.Deserialize<WireStreamResponse>(
                                         eventData,
                                         WireJsonOptions)
                                     ?? throw new OpenAiProtocolException(
                                         "The AI provider returned an empty SSE payload.");
                }
                catch (JsonException exception)
                {
                    throw new OpenAiProtocolException(
                        "The AI provider returned invalid JSON in an SSE event.",
                        exception);
                }

                if (streamResponse.Choices is null)
                {
                    throw new OpenAiProtocolException("An SSE payload omitted its choices array.");
                }

                AiTokenUsage? usage = MapUsage(streamResponse.Usage);
                if (usage is not null)
                {
                    yield return new AiCompletionChunk(AiCompletionChunkKind.Content, string.Empty, usage);
                }

                foreach (WireStreamChoice choice in streamResponse.Choices)
                {
                    if (!string.IsNullOrEmpty(choice.FinishReason))
                    {
                        sawTerminalSignal = true;
                        finishReason ??= choice.FinishReason;
                    }

                    if (choice.Delta is null)
                    {
                        continue;
                    }

                    string reasoning = GetReasoningContent(choice.Delta);
                    if (reasoning.Length > 0)
                    {
                        EnsureStreamingSize(
                            streamedReasoningCharacters,
                            reasoning.Length,
                            "reasoning");
                        streamedReasoningCharacters += reasoning.Length;
                        yield return new AiCompletionChunk(
                            AiCompletionChunkKind.Reasoning,
                            reasoning);
                    }

                    string content = GetContent(choice.Delta.Content);
                    if (content.Length > 0)
                    {
                        IReadOnlyList<AiCompletionChunk> parsedChunks = thinkTagParser is null
                            ? [new AiCompletionChunk(AiCompletionChunkKind.Content, content)]
                            : thinkTagParser.Push(content);
                        foreach (AiCompletionChunk parsedChunk in parsedChunks)
                        {
                            if (parsedChunk.Text.Length == 0)
                            {
                                continue;
                            }

                            if (parsedChunk.Kind == AiCompletionChunkKind.Reasoning)
                            {
                                EnsureStreamingSize(
                                    streamedReasoningCharacters,
                                    parsedChunk.Text.Length,
                                    "reasoning");
                                streamedReasoningCharacters += parsedChunk.Text.Length;
                            }
                            else
                            {
                                EnsureStreamingSize(
                                    streamedContentCharacters,
                                    parsedChunk.Text.Length,
                                    "content");
                                streamedContentCharacters += parsedChunk.Text.Length;
                            }

                            yield return parsedChunk;
                        }
                    }
                }
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        if (thinkTagParser is not null)
        {
            foreach (AiCompletionChunk trailingChunk in thinkTagParser.Flush())
            {
                if (trailingChunk.Text.Length == 0)
                {
                    continue;
                }

                if (trailingChunk.Kind == AiCompletionChunkKind.Reasoning)
                {
                    EnsureStreamingSize(
                        streamedReasoningCharacters,
                        trailingChunk.Text.Length,
                        "reasoning");
                    streamedReasoningCharacters += trailingChunk.Text.Length;
                }
                else
                {
                    EnsureStreamingSize(
                        streamedContentCharacters,
                        trailingChunk.Text.Length,
                        "content");
                    streamedContentCharacters += trailingChunk.Text.Length;
                }

                yield return trailingChunk;
            }
        }

        if (!sawTerminalSignal)
        {
            throw new OpenAiProtocolException(
                "The streaming AI response ended before a completion signal was received.");
        }

        yield return new AiCompletionChunk(
            AiCompletionChunkKind.Completion,
            string.Empty,
            FinishReason: finishReason);
    }

    internal async IAsyncEnumerable<AiAgentStreamChunk> StreamAgentAsync(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        IReadOnlyList<AiToolDefinition> tools,
        AiToolChoice toolChoice,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateTimeoutSource(cancellationToken);
        using HttpResponseMessage response = await SendWithReasoningFallbackAsync(
                endpoint,
                model,
                apiKey,
                messages,
                temperature,
                provider,
                reasoningEffort,
                tools,
                toolChoice,
                stream: true,
                timeoutSource.Token)
            .ConfigureAwait(false);

        await EnsureSuccessWithTimeoutAsync(response, timeoutSource, cancellationToken).ConfigureAwait(false);
        await using Stream responseStream = await ReadResponseStreamWithTimeoutAsync(
                response.Content,
                timeoutSource,
                cancellationToken)
            .ConfigureAwait(false);
        IAsyncEnumerator<SseEvent> events = SseParser
            .ReadEventsAsync(responseStream, cancellationToken: timeoutSource.Token)
            .GetAsyncEnumerator(timeoutSource.Token);

        bool sawTerminalSignal = false;
        string? finishReason = null;
        int streamedContentCharacters = 0;
        int streamedReasoningCharacters = 0;
        int streamedToolCharacters = 0;
        ThinkTagStreamParser? thinkTagParser = provider == AiProvider.Custom
            ? new ThinkTagStreamParser()
            : null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                {
                    throw new TimeoutException("The streaming AI request timed out.", exception);
                }

                if (!hasNext)
                {
                    break;
                }

                SseEvent serverEvent = events.Current;
                string eventData = serverEvent.Data;
                if (eventData.AsSpan().Trim().SequenceEqual("[DONE]"))
                {
                    sawTerminalSignal = true;
                    break;
                }

                if (string.Equals(serverEvent.EventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OpenAiProtocolException("The AI provider returned an SSE error event.");
                }

                WireStreamResponse streamResponse;
                try
                {
                    streamResponse = JsonSerializer.Deserialize<WireStreamResponse>(
                                         eventData,
                                         WireJsonOptions)
                                     ?? throw new OpenAiProtocolException(
                                         "The AI provider returned an empty SSE payload.");
                }
                catch (JsonException exception)
                {
                    throw new OpenAiProtocolException(
                        "The AI provider returned invalid JSON in an SSE event.",
                        exception);
                }

                if (streamResponse.Choices is null)
                {
                    throw new OpenAiProtocolException("An SSE payload omitted its choices array.");
                }

                AiTokenUsage? usage = MapUsage(streamResponse.Usage);
                if (usage is not null)
                {
                    yield return new AiAgentStreamChunk(AiAgentStreamChunkKind.Content, string.Empty, Usage: usage);
                }

                foreach (WireStreamChoice choice in streamResponse.Choices)
                {
                    if (!string.IsNullOrEmpty(choice.FinishReason))
                    {
                        sawTerminalSignal = true;
                        finishReason ??= choice.FinishReason;
                    }

                    if (choice.Delta is null)
                    {
                        continue;
                    }

                    string reasoning = GetReasoningContent(choice.Delta);
                    if (reasoning.Length > 0)
                    {
                        EnsureStreamingSize(
                            streamedReasoningCharacters,
                            reasoning.Length,
                            "reasoning");
                        streamedReasoningCharacters += reasoning.Length;
                        yield return new AiAgentStreamChunk(
                            AiAgentStreamChunkKind.Reasoning,
                            reasoning);
                    }

                    string content = GetContent(choice.Delta.Content);
                    if (content.Length > 0)
                    {
                        IReadOnlyList<AiCompletionChunk> parsedChunks = thinkTagParser is null
                            ? [new AiCompletionChunk(AiCompletionChunkKind.Content, content)]
                            : thinkTagParser.Push(content);
                        foreach (AiCompletionChunk parsedChunk in parsedChunks)
                        {
                            if (parsedChunk.Text.Length == 0)
                            {
                                continue;
                            }

                            AiAgentStreamChunkKind kind = parsedChunk.Kind == AiCompletionChunkKind.Reasoning
                                ? AiAgentStreamChunkKind.Reasoning
                                : AiAgentStreamChunkKind.Content;
                            if (kind == AiAgentStreamChunkKind.Reasoning)
                            {
                                EnsureStreamingSize(
                                    streamedReasoningCharacters,
                                    parsedChunk.Text.Length,
                                    "reasoning");
                                streamedReasoningCharacters += parsedChunk.Text.Length;
                            }
                            else
                            {
                                EnsureStreamingSize(
                                    streamedContentCharacters,
                                    parsedChunk.Text.Length,
                                    "content");
                                streamedContentCharacters += parsedChunk.Text.Length;
                            }

                            yield return new AiAgentStreamChunk(kind, parsedChunk.Text);
                        }
                    }

                    if (choice.Delta.ToolCalls is null)
                    {
                        continue;
                    }

                    for (int position = 0; position < choice.Delta.ToolCalls.Count; position++)
                    {
                        WireStreamToolCall toolCall = choice.Delta.ToolCalls[position];
                        string arguments = GetStreamingToolArgumentFragment(
                            toolCall.Function?.Arguments ?? default);
                        int addedLength = arguments.Length +
                                          (toolCall.Id?.Length ?? 0) +
                                          (toolCall.Function?.Name?.Length ?? 0);
                        EnsureStreamingSize(streamedToolCharacters, addedLength, "tool calls");
                        streamedToolCharacters += addedLength;
                        yield return new AiAgentStreamChunk(
                            AiAgentStreamChunkKind.ToolCall,
                            arguments,
                            toolCall.Index ?? position,
                            toolCall.Id,
                            toolCall.Function?.Name);
                    }
                }
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        if (thinkTagParser is not null)
        {
            foreach (AiCompletionChunk trailingChunk in thinkTagParser.Flush())
            {
                if (trailingChunk.Text.Length == 0)
                {
                    continue;
                }

                AiAgentStreamChunkKind kind = trailingChunk.Kind == AiCompletionChunkKind.Reasoning
                    ? AiAgentStreamChunkKind.Reasoning
                    : AiAgentStreamChunkKind.Content;
                if (kind == AiAgentStreamChunkKind.Reasoning)
                {
                    EnsureStreamingSize(
                        streamedReasoningCharacters,
                        trailingChunk.Text.Length,
                        "reasoning");
                    streamedReasoningCharacters += trailingChunk.Text.Length;
                }
                else
                {
                    EnsureStreamingSize(
                        streamedContentCharacters,
                        trailingChunk.Text.Length,
                        "content");
                    streamedContentCharacters += trailingChunk.Text.Length;
                }

                yield return new AiAgentStreamChunk(kind, trailingChunk.Text);
            }
        }

        if (!sawTerminalSignal)
        {
            throw new OpenAiProtocolException(
                "The streaming AI response ended before a completion signal was received.");
        }

        yield return new AiAgentStreamChunk(
            AiAgentStreamChunkKind.Completion,
            string.Empty,
            FinishReason: finishReason);
    }

    private static IReadOnlyList<WireToolDefinition>? BuildWireTools(
        IReadOnlyList<AiToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        return tools
            .Select(tool => new WireToolDefinition(
                "function",
                new WireFunctionDefinition(
                    tool.Name,
                    tool.Description,
                    tool.Parameters)))
            .ToArray();
    }

    private static string MapToolChoice(AiToolChoice toolChoice) => toolChoice switch
    {
        AiToolChoice.Auto => "auto",
        AiToolChoice.None => "none",
        AiToolChoice.Required => "required",
        _ => "auto",
    };

    private static AiToolCall ParseToolCall(WireToolCall toolCall)
    {
        if (toolCall.Function is null || string.IsNullOrWhiteSpace(toolCall.Function.Name))
        {
            throw new OpenAiProtocolException("The AI response contained an invalid tool call.");
        }

        return new AiToolCall(
            toolCall.Id ?? string.Empty,
            toolCall.Function.Name,
            GetToolArguments(toolCall.Function.Arguments));
    }

    private static AiToolCall ParseLegacyFunctionCall(WireFunctionCall functionCall)
    {
        if (string.IsNullOrWhiteSpace(functionCall.Name))
        {
            throw new OpenAiProtocolException("The AI response contained an invalid function call.");
        }

        return new AiToolCall(
            string.Empty,
            functionCall.Name,
            GetToolArguments(functionCall.Arguments));
    }

    private static string GetToolArguments(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.String => arguments.GetString() ?? "{}",
        JsonValueKind.Object or JsonValueKind.Array => arguments.GetRawText(),
        JsonValueKind.Undefined or JsonValueKind.Null => "{}",
        _ => throw new OpenAiProtocolException(
            "The AI response used an unsupported tool arguments shape."),
    };

    private static string GetStreamingToolArgumentFragment(JsonElement arguments) =>
        arguments.ValueKind switch
        {
            JsonValueKind.String => arguments.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => arguments.GetRawText(),
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => throw new OpenAiProtocolException(
                "The streaming AI response used an unsupported tool arguments shape."),
        };

    private static HttpRequestMessage CreateRequest(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        IReadOnlyList<AiToolDefinition>? tools,
        AiToolChoice toolChoice,
        bool stream,
        bool includeReasoningParameters)
    {
        ValidateRequest(endpoint, model, apiKey, messages, temperature);

        var wireMessages = new List<WireRequestMessage>(messages.Count);
        foreach (OpenAiWireMessage message in messages)
        {
            IReadOnlyList<WireRequestToolCall>? toolCalls = message.ToolCalls.Count == 0
                ? null
                : message.ToolCalls.Select(call => new WireRequestToolCall(
                    string.IsNullOrWhiteSpace(call.Id) ? $"call_{Guid.NewGuid():N}" : call.Id,
                    "function",
                    new WireRequestFunction(call.Name, call.Arguments))).ToArray();
            wireMessages.Add(new WireRequestMessage
            {
                Role = message.Role,
                Content = toolCalls is not null && message.Content.Length == 0
                    ? null
                    : message.Content,
                ToolCalls = toolCalls,
                ToolCallId = message.ToolCallId,
            });
        }

        ReasoningWireOptions reasoning = includeReasoningParameters
            ? ReasoningWireOptions.Resolve(provider, endpoint, model, reasoningEffort)
            : ReasoningWireOptions.None;
        var payload = new WireCompletionRequest
        {
            Model = model,
            Messages = wireMessages,
            Stream = stream,
            StreamOptions = stream ? new WireStreamOptions { IncludeUsage = true } : null,
            Temperature = reasoningEffort == AiReasoningEffort.Off || !includeReasoningParameters
                ? temperature
                : null,
            Thinking = reasoning.Thinking,
            ReasoningEffort = reasoning.ReasoningEffort,
            EnableThinking = reasoning.EnableThinking,
            ThinkingBudget = reasoning.ThinkingBudget,
            ReasoningSplit = reasoning.ReasoningSplit,
            Tools = BuildWireTools(tools),
            ToolChoice = tools is { Count: > 0 } ? MapToolChoice(toolChoice) : null,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: WireJsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            stream ? "text/event-stream" : "application/json"));
        return request;
    }

    private static void ValidateRequest(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new ArgumentException(
                "AI credentials may only be sent over HTTPS or to a loopback HTTP endpoint.",
                nameof(endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(apiKey) && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "An API key is required unless the endpoint is a loopback address.",
                nameof(apiKey));
        }
        ArgumentNullException.ThrowIfNull(messages);

        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The AI endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one AI message is required.", nameof(messages));
        }

        foreach (OpenAiWireMessage message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Role) || message.Content is null)
            {
                throw new ArgumentException("Every AI message requires a role and content.", nameof(messages));
            }


            if (message.Role == "tool" && string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new ArgumentException("Every tool message requires a tool call identifier.", nameof(messages));
            }
        }

        if (temperature is double value && !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature));
        }
    }

    private async Task<HttpResponseMessage> SendWithReasoningFallbackAsync(
        Uri endpoint,
        string model,
        string apiKey,
        IReadOnlyList<OpenAiWireMessage> messages,
        double? temperature,
        AiProvider provider,
        AiReasoningEffort reasoningEffort,
        IReadOnlyList<AiToolDefinition>? tools,
        AiToolChoice toolChoice,
        bool stream,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            endpoint,
            model,
            apiKey,
            messages,
            temperature,
            provider,
            reasoningEffort,
            tools,
            toolChoice,
            stream,
            includeReasoningParameters: true);
        HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (provider != AiProvider.Custom ||
            response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
        {
            return response;
        }

        try
        {
            await DrainErrorContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }

        using HttpRequestMessage fallbackRequest = CreateRequest(
            endpoint,
            model,
            apiKey,
            messages,
            temperature,
            provider,
            reasoningEffort,
            tools,
            toolChoice,
            stream,
            includeReasoningParameters: false);
        return await _httpClient.SendAsync(
                fallbackRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureSuccessWithTimeoutAsync(
        HttpResponseMessage response,
        CancellationTokenSource timeoutSource,
        CancellationToken callerToken)
    {
        try
        {
            await EnsureSuccessAsync(response, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !callerToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("The streaming AI request timed out.", exception);
        }
    }

    private static async Task<Stream> ReadResponseStreamWithTimeoutAsync(
        HttpContent content,
        CancellationTokenSource timeoutSource,
        CancellationToken callerToken)
    {
        try
        {
            return await content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !callerToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("The streaming AI request timed out.", exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await DrainErrorContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        throw new OpenAiHttpException(response.StatusCode);
    }

    private static async Task DrainErrorContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumDrainBytes = 64 * 1024;
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[4096];
        int totalBytes = 0;

        while (totalBytes < maximumDrainBytes)
        {
            int bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, maximumDrainBytes - totalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
        }

        CryptographicOperations.ZeroMemory(buffer);
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > maximumBytes))
        {
            throw new OpenAiProtocolException("The AI response exceeded the configured size limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        long? declaredLength = content.Headers.ContentLength;
        using var buffer = new MemoryStream(
            declaredLength is > 0 && declaredLength <= maximumBytes
                ? checked((int)declaredLength.Value)
                : 0);
        byte[] readBuffer = new byte[ReadBufferSize];

        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (buffer.Length > maximumBytes - bytesRead)
                {
                    throw new OpenAiProtocolException("The AI response exceeded the configured size limit.");
                }

                await buffer.WriteAsync(readBuffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readBuffer);
        }
    }

    private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(_requestTimeout);
        }

        return source;
    }

    private static void AppendContent(StringBuilder destination, JsonElement content)
    {
        string value = GetContent(content);
        if (value.Length > 0)
        {
            destination.Append(value);
        }
    }

    private static void AppendReasoningDetails(StringBuilder destination, JsonElement details)
    {
        string value = GetOptionalText(details);
        if (value.Length > 0)
        {
            destination.Append(value);
        }
    }

    private static void EnsureStreamingSize(int currentLength, int addedLength, string fieldName)
    {
        if (currentLength > MaximumStreamingContentCharacters - addedLength)
        {
            throw new OpenAiProtocolException(
                $"The streaming AI {fieldName} exceeded the configured size limit.");
        }
    }

    private static string GetReasoningContent(WireDelta delta)
    {
        foreach (JsonElement candidate in new[]
                 {
                     delta.ReasoningContent,
                     delta.ReasoningDetails,
                     delta.Reasoning,
                     delta.Analysis,
                     delta.ThinkingContent,
                 })
        {
            string content = GetOptionalText(candidate);
            if (content.Length > 0)
            {
                return content;
            }
        }

        return string.Empty;
    }

    private static string GetOptionalText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                {
                    var result = new StringBuilder();
                    foreach (JsonElement part in value.EnumerateArray())
                    {
                        result.Append(GetOptionalText(part));
                    }

                    return result.ToString();
                }
            case JsonValueKind.Object:
                foreach (string propertyName in new[] { "text", "content", "data", "summary" })
                {
                    if (value.TryGetProperty(propertyName, out JsonElement property))
                    {
                        string text = GetOptionalText(property);
                        if (text.Length > 0)
                        {
                            return text;
                        }
                    }
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static AiTokenUsage? MapUsage(WireUsage? usage)
    {
        if (usage is null || usage.PromptTokens is null || usage.CompletionTokens is null || usage.TotalTokens is null)
        {
            return null;
        }

        int reasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens ?? 0;
        int cachedTokens = usage.PromptTokensDetails?.CachedTokens ?? 0;
        var mapped = new AiTokenUsage(
            usage.PromptTokens.Value,
            usage.CompletionTokens.Value,
            usage.TotalTokens.Value,
            reasoningTokens,
            cachedTokens);
        return mapped.IsValid ? mapped : null;
    }

    private static string GetContent(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                {
                    var result = new StringBuilder();
                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String)
                        {
                            result.Append(part.GetString());
                            continue;
                        }

                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("text", out JsonElement text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            result.Append(text.GetString());
                        }
                    }

                    return result.ToString();
                }
            default:
                throw new OpenAiProtocolException("The AI response used an unsupported content shape.");
        }
    }

    private static JsonSerializerOptions CreateWireJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class WireCompletionRequest
    {
        public required string Model { get; init; }

        public required IReadOnlyList<WireRequestMessage> Messages { get; init; }

        public bool Stream { get; init; }

        [JsonPropertyName("stream_options")]
        public WireStreamOptions? StreamOptions { get; init; }

        public double? Temperature { get; init; }

        public WireThinking? Thinking { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("enable_thinking")]
        public bool? EnableThinking { get; init; }

        [JsonPropertyName("thinking_budget")]
        public int? ThinkingBudget { get; init; }

        [JsonPropertyName("reasoning_split")]
        public bool? ReasoningSplit { get; init; }

        public IReadOnlyList<WireToolDefinition>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; init; }
    }

    private sealed record WireThinking(string Type);

    private sealed class WireStreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; init; }
    }

    private sealed class WireRequestMessage
    {
        public required string Role { get; init; }

        public string? Content { get; init; }

        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<WireRequestToolCall>? ToolCalls { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }
    }

    private sealed record WireRequestToolCall(
        string Id,
        string Type,
        WireRequestFunction Function);

    private sealed record WireRequestFunction(
        string Name,
        string Arguments);

    private sealed record WireToolDefinition(string Type, WireFunctionDefinition Function);

    private sealed record WireFunctionDefinition(
        string Name,
        string Description,
        JsonElement Parameters);

    private sealed class WireCompletionResponse
    {
        public List<WireChoice>? Choices { get; init; }

        public WireUsage? Usage { get; init; }
    }

    private sealed class WireChoice
    {
        public WireResponseMessage? Message { get; init; }
    }

    private sealed class WireResponseMessage
    {
        public JsonElement Content { get; init; }

        [JsonPropertyName("reasoning_content")]
        public JsonElement ReasoningContent { get; init; }

        [JsonPropertyName("reasoning_details")]
        public JsonElement ReasoningDetails { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<WireToolCall>? ToolCalls { get; init; }

        [JsonPropertyName("function_call")]
        public WireFunctionCall? FunctionCall { get; init; }
    }

    private sealed class WireToolCall
    {
        public string? Id { get; init; }

        public string? Type { get; init; }

        public WireFunctionCall? Function { get; init; }
    }

    private sealed class WireFunctionCall
    {
        public string? Name { get; init; }

        public JsonElement Arguments { get; init; }
    }

    private sealed class WireStreamResponse
    {
        public List<WireStreamChoice>? Choices { get; init; }

        public WireUsage? Usage { get; init; }
    }

    private sealed class WireUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; init; }

        [JsonPropertyName("total_tokens")]
        public int? TotalTokens { get; init; }

        [JsonPropertyName("completion_tokens_details")]
        public WireCompletionTokensDetails? CompletionTokensDetails { get; init; }

        [JsonPropertyName("prompt_tokens_details")]
        public WirePromptTokensDetails? PromptTokensDetails { get; init; }
    }

    private sealed class WireCompletionTokensDetails
    {
        [JsonPropertyName("reasoning_tokens")]
        public int? ReasoningTokens { get; init; }
    }

    private sealed class WirePromptTokensDetails
    {
        [JsonPropertyName("cached_tokens")]
        public int? CachedTokens { get; init; }
    }

    private sealed class WireStreamChoice
    {
        public WireDelta? Delta { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    private sealed class WireDelta
    {
        public JsonElement Content { get; init; }

        [JsonPropertyName("reasoning_content")]
        public JsonElement ReasoningContent { get; init; }

        [JsonPropertyName("reasoning_details")]
        public JsonElement ReasoningDetails { get; init; }

        public JsonElement Reasoning { get; init; }

        public JsonElement Analysis { get; init; }

        [JsonPropertyName("thinking_content")]
        public JsonElement ThinkingContent { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<WireStreamToolCall>? ToolCalls { get; init; }
    }

    private sealed class WireStreamToolCall
    {
        public int? Index { get; init; }

        public string? Id { get; init; }

        public string? Type { get; init; }

        public WireStreamFunction? Function { get; init; }
    }

    private sealed class WireStreamFunction
    {
        public string? Name { get; init; }

        public JsonElement Arguments { get; init; }
    }

    private sealed class ThinkTagStreamParser
    {
        private static readonly string[] OpeningTags = ["<think>", "<analysis>"];
        private readonly StringBuilder _pending = new();
        private bool _insideReasoning;
        private string _closingTag = string.Empty;

        internal IReadOnlyList<AiCompletionChunk> Push(string content)
        {
            _pending.Append(content);
            return Drain(final: false);
        }

        internal IReadOnlyList<AiCompletionChunk> Flush() => Drain(final: true);

        private IReadOnlyList<AiCompletionChunk> Drain(bool final)
        {
            var result = new List<AiCompletionChunk>();
            while (_pending.Length > 0)
            {
                string pending = _pending.ToString();
                if (_insideReasoning)
                {
                    int closingIndex = pending.IndexOf(
                        _closingTag,
                        StringComparison.OrdinalIgnoreCase);
                    if (closingIndex >= 0)
                    {
                        AddChunk(result, AiCompletionChunkKind.Reasoning, pending[..closingIndex]);
                        _pending.Remove(0, closingIndex + _closingTag.Length);
                        _insideReasoning = false;
                        _closingTag = string.Empty;
                        continue;
                    }

                    int retainedLength = final
                        ? 0
                        : GetPartialTagSuffixLength(pending, [_closingTag]);
                    AddChunk(
                        result,
                        AiCompletionChunkKind.Reasoning,
                        pending[..(pending.Length - retainedLength)]);
                    _pending.Remove(0, pending.Length - retainedLength);
                    break;
                }

                (int openingIndex, string openingTag) = FindOpeningTag(pending);
                if (openingIndex >= 0)
                {
                    AddChunk(result, AiCompletionChunkKind.Content, pending[..openingIndex]);
                    _pending.Remove(0, openingIndex + openingTag.Length);
                    _insideReasoning = true;
                    _closingTag = openingTag.Equals("<analysis>", StringComparison.OrdinalIgnoreCase)
                        ? "</analysis>"
                        : "</think>";
                    continue;
                }

                int retainedOpeningLength = final
                    ? 0
                    : GetPartialTagSuffixLength(pending, OpeningTags);
                AddChunk(
                    result,
                    AiCompletionChunkKind.Content,
                    pending[..(pending.Length - retainedOpeningLength)]);
                _pending.Remove(0, pending.Length - retainedOpeningLength);
                break;
            }

            return result;
        }

        private static (int Index, string Tag) FindOpeningTag(string value)
        {
            int bestIndex = -1;
            string bestTag = string.Empty;
            foreach (string tag in OpeningTags)
            {
                int index = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (bestIndex < 0 || index < bestIndex))
                {
                    bestIndex = index;
                    bestTag = tag;
                }
            }

            return (bestIndex, bestTag);
        }

        private static int GetPartialTagSuffixLength(string value, IReadOnlyList<string> tags)
        {
            int bestLength = 0;
            foreach (string tag in tags)
            {
                int maximumLength = Math.Min(value.Length, tag.Length - 1);
                for (int length = maximumLength; length > bestLength; length--)
                {
                    if (value.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
                    {
                        bestLength = length;
                        break;
                    }
                }
            }

            return bestLength;
        }

        private static void AddChunk(
            ICollection<AiCompletionChunk> destination,
            AiCompletionChunkKind kind,
            string text)
        {
            if (text.Length > 0)
            {
                destination.Add(new AiCompletionChunk(kind, text));
            }
        }
    }

    private enum ReasoningProviderKind
    {
        Standard,
        DeepSeek,
        ChatGlm,
        QianWen,
        Minimax,
    }

    private sealed record ReasoningWireOptions(
        WireThinking? Thinking,
        string? ReasoningEffort,
        bool? EnableThinking,
        int? ThinkingBudget,
        bool? ReasoningSplit)
    {
        internal static ReasoningWireOptions None { get; } = new(null, null, null, null, null);

        internal static ReasoningWireOptions Resolve(
            AiProvider provider,
            Uri endpoint,
            string model,
            AiReasoningEffort effort)
        {
            ReasoningProviderKind kind = ResolveProviderKind(provider, endpoint, model);
            bool enabled = effort != AiReasoningEffort.Off;
            return kind switch
            {
                ReasoningProviderKind.DeepSeek => new ReasoningWireOptions(
                    new WireThinking(enabled ? "enabled" : "disabled"),
                    enabled ? effort == AiReasoningEffort.Max ? "max" : "high" : null,
                    null,
                    null,
                    null),
                ReasoningProviderKind.ChatGlm => new ReasoningWireOptions(
                    new WireThinking(enabled ? "enabled" : "disabled"),
                    enabled && model.StartsWith("glm-5.2", StringComparison.OrdinalIgnoreCase)
                        ? MapStandardEffort(effort)
                        : null,
                    null,
                    null,
                    null),
                ReasoningProviderKind.QianWen => new ReasoningWireOptions(
                    null,
                    null,
                    enabled,
                    effort switch
                    {
                        AiReasoningEffort.Low => 1024,
                        AiReasoningEffort.High => 8192,
                        _ => null,
                    },
                    null),
                ReasoningProviderKind.Minimax => new ReasoningWireOptions(
                    new WireThinking(enabled ? "adaptive" : "disabled"),
                    null,
                    null,
                    null,
                    enabled),
                _ => new ReasoningWireOptions(
                    null,
                    MapStandardEffort(effort),
                    null,
                    null,
                    null),
            };
        }

        private static string MapStandardEffort(AiReasoningEffort effort) => effort switch
        {
            AiReasoningEffort.Off => "none",
            AiReasoningEffort.Low => "low",
            AiReasoningEffort.High => "high",
            AiReasoningEffort.Max => "max",
            _ => "none",
        };

        private static ReasoningProviderKind ResolveProviderKind(
            AiProvider provider,
            Uri endpoint,
            string model)
        {
            if (provider != AiProvider.Custom)
            {
                return provider switch
                {
                    AiProvider.DeepSeek => ReasoningProviderKind.DeepSeek,
                    AiProvider.ChatGlm => ReasoningProviderKind.ChatGlm,
                    AiProvider.QianWen => ReasoningProviderKind.QianWen,
                    AiProvider.Minimax => ReasoningProviderKind.Minimax,
                    _ => ReasoningProviderKind.Standard,
                };
            }

            string host = endpoint.Host;
            if (host.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                return ReasoningProviderKind.DeepSeek;
            }

            if (host.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("zhipu", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase))
            {
                return ReasoningProviderKind.ChatGlm;
            }

            if (host.Contains("dashscope", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("aliyuncs", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("qwq", StringComparison.OrdinalIgnoreCase))
            {
                return ReasoningProviderKind.QianWen;
            }

            if (host.Contains("minimax", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("minimax", StringComparison.OrdinalIgnoreCase))
            {
                return ReasoningProviderKind.Minimax;
            }

            return ReasoningProviderKind.Standard;
        }
    }

    private static class SharedOpenAiHttpClient
    {
        private static readonly HttpClient Client = CreateClient();

        internal static HttpClient Instance => Client;
        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip |
                                         DecompressionMethods.Deflate |
                                         DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = 8,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                UseCookies = false,
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
    }
}

internal sealed class OpenAiHttpException : HttpRequestException
{
    internal OpenAiHttpException(HttpStatusCode statusCode)
        : base(
            $"The AI provider returned HTTP status {(int)statusCode}.",
            inner: null,
            statusCode)
    {
    }
}

internal sealed class OpenAiProtocolException : IOException
{
    internal OpenAiProtocolException(string message)
        : base(message)
    {
    }

    internal OpenAiProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
