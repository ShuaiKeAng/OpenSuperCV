using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SuperCV.Domain.AI;
using static SuperCV.CustomInstructionsManager;

namespace SuperCV
{
    public partial class AIChatbot : Window, INotifyPropertyChanged
    {
        private const int MaximumTransientRetryCount = 2;
        private static IReadOnlyList<ReasoningDepthOption> CreateReasoningDepthOptions() =>
        [
            new(AiReasoningEffort.Off, LocalizationService.Current.T("思考：关闭"), LocalizationService.Current.T("直接回答；自定义接口不支持参数时也会自动回退")),
            new(AiReasoningEffort.Low, LocalizationService.Current.T("思考：较弱"), LocalizationService.Current.T("较短思考，优先速度与较低用量")),
            new(AiReasoningEffort.High, LocalizationService.Current.T("思考：较高"), LocalizationService.Current.T("更充分思考，兼顾质量与耗时")),
            new(AiReasoningEffort.Max, LocalizationService.Current.T("思考：最高"), LocalizationService.Current.T("使用服务商允许的最高思考强度")),
        ];

        private static IReadOnlyList<AgentAccessOption> CreateAgentAccessOptions() =>
        [
            new(AgentAccessLevel.ReadOnly, LocalizationService.Current.T("Agent：只读"), LocalizationService.Current.T("允许读取、筛选与定位，不允许修改条目")),
            new(AgentAccessLevel.ApprovalRequired, LocalizationService.Current.T("Agent：审核批准"), LocalizationService.Current.T("每次写入、删除或标记前复用确认弹窗")),
            new(AgentAccessLevel.FullAccess, LocalizationService.Current.T("Agent：完全访问"), LocalizationService.Current.T("允许 Agent 直接修改当前工作区条目")),
        ];

        private ObservableCollection<ChatMessage> _messages = new();
        private string _contentObject = string.Empty;
        private string _initialQuery = string.Empty;
        private string _inputText = string.Empty;
        private string _contentTitle="";
        private bool _autoScroll = true; // 是否自动滚动到底部
        private readonly string _systemInstruction;
        private readonly AgentTool _agentTool;
        private readonly AgentLoop _agentLoop;
        private readonly CancellationTokenSource _windowCancellation = new();
        private readonly CancellationToken _windowToken;
        private CancellationTokenSource? _responseCancellation;
        private ChatMessage? _messageBeingEdited;
        private bool _agentConversationNeedsRebuild;
        private bool _isBusy;
        private bool _resourcesReleased;
        private ReasoningDepthOption _selectedReasoningDepth = CreateReasoningDepthOptions()[0];
        private AgentAccessOption _selectedAgentAccess = CreateAgentAccessOptions()[1];
        public ObservableCollection<ChatMessage> Messages
        {
            get { return _messages; }
            set
            {
                _messages = value;
                OnPropertyChanged(nameof(Messages));
            }
        }
        public string ContentTitle
        {
            get { return _contentTitle; }
            set { _contentTitle = value ?? string.Empty; OnPropertyChanged(nameof(ContentTitle));}
        }

        public string ContentObject
        {
            get { return _contentObject; }
            set
            {
                _contentObject = value ?? string.Empty;
                OnPropertyChanged(nameof(ContentObject));
                OnPropertyChanged(nameof(ContentShow));
            }
        }
        public string ContentShow
        {
            get { return _contentObject.Replace("\r\n", "").Replace("\n", ""); }
        }

        public string InitialQuery
        {
            get { return _initialQuery; }
            set
            {
                _initialQuery = value ?? string.Empty;
                OnPropertyChanged(nameof(InitialQuery));
            }
        }

        public string InputText
        {
            get { return _inputText; }
            set
            {
                _inputText = value ?? string.Empty;
                OnPropertyChanged(nameof(InputText));
            }
        }

        public Visibility SourcePanelVisibility { get; }

        public string SourceTokenEstimate { get; }

        public Visibility SourceTokenEstimateVisibility { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value)
                {
                    return;
                }

                _isBusy = value;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanSend));
            }
        }

        public bool CanSend => !IsBusy;

        public IReadOnlyList<ReasoningDepthOption> ReasoningDepthOptions => CreateReasoningDepthOptions();

        public IReadOnlyList<AgentAccessOption> AgentAccessOptions => CreateAgentAccessOptions();

        public AgentAccessOption SelectedAgentAccess
        {
            get => _selectedAgentAccess;
            set
            {
                if (value is null || _selectedAgentAccess.Value == value.Value)
                {
                    return;
                }

                _selectedAgentAccess = value;
                OnPropertyChanged(nameof(SelectedAgentAccess));
            }
        }

        public ReasoningDepthOption SelectedReasoningDepth
        {
            get => _selectedReasoningDepth;
            set
            {
                if (value is null || _selectedReasoningDepth.Value == value.Value)
                {
                    return;
                }

                _selectedReasoningDepth = value;
                OnPropertyChanged(nameof(SelectedReasoningDepth));
                Setting.AIChatReasoningEffort = value.Value;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public AIChatbot(
            string contentObject,
            string initialQuery,
            bool showSource = true,
            string? systemInstruction = null,
            string? contentTitle = null,
            int? sourceTokenCount = null,
            AgentTool? agentTool = null)
        {
            InitializeComponent();
            _selectedReasoningDepth = ReasoningDepthOptions.FirstOrDefault(
                option => option.Value == Setting.AIChatReasoningEffort)
                ?? ReasoningDepthOptions[0];
            _windowToken = _windowCancellation.Token;
            _systemInstruction = systemInstruction ?? string.Empty;
            AppRuntime runtime = GetRuntime();
            _agentTool = agentTool ?? new AgentTool(runtime);
            _agentLoop = new AgentLoop(
                new AI2(runtime, Setting.APIKey, Setting.AIModel, Setting.AIPromptContent),
                _agentTool,
                () => runtime.Settings.Snapshot.MaxAiContextTokens);
            SourcePanelVisibility = showSource ? Visibility.Visible : Visibility.Collapsed;
            SourceTokenEstimate = FormatTokenEstimate(sourceTokenCount);
            SourceTokenEstimateVisibility = sourceTokenCount.HasValue
                ? Visibility.Visible
                : Visibility.Collapsed;
            ContentTitle = contentTitle ?? "...";
            ContentObject = contentObject ?? string.Empty;
            InitialQuery = initialQuery ?? string.Empty;
            Messages = new ObservableCollection<ChatMessage>();

            DataContext = this;
            Loaded += AIChatbot_Loaded;
            AiFeatureAvailabilityState.Current.Changed += OnAiFeatureAvailabilityChanged;
            LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;

            if (AiFeatureAvailabilityState.Current.IsEnabled)
            {
                if (!string.IsNullOrWhiteSpace(InitialQuery))
                {
                    // 添加初始对话
                    AddInitialMessages();
                }

                if (showSource && !string.IsNullOrWhiteSpace(ContentObject))
                {
                    _ = GetTitleAsync();
                }
            }
        }

        internal static string FormatTokenEstimate(int? tokenCount) =>
            tokenCount is >= 0 ? $"{tokenCount.Value:N0} tokens" : string.Empty;

        protected override void OnClosed(EventArgs e)
        {
            Loaded -= AIChatbot_Loaded;
            AiFeatureAvailabilityState.Current.Changed -= OnAiFeatureAvailabilityChanged;
            LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
            if (!_resourcesReleased)
            {
                _resourcesReleased = true;
                try
                {
                    _windowCancellation.Cancel();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                }
                finally
                {
                    _agentTool.ClearAgentFilter();
                    _windowCancellation.Dispose();
                }
            }

            base.OnClosed(e);
        }

        private void Localization_LanguageChanged(object? sender, EventArgs e)
        {
            AiReasoningEffort reasoning = _selectedReasoningDepth.Value;
            AgentAccessLevel access = _selectedAgentAccess.Value;
            _selectedReasoningDepth = ReasoningDepthOptions.First(option => option.Value == reasoning);
            _selectedAgentAccess = AgentAccessOptions.First(option => option.Value == access);
            OnPropertyChanged(nameof(ReasoningDepthOptions));
            OnPropertyChanged(nameof(AgentAccessOptions));
            OnPropertyChanged(nameof(SelectedReasoningDepth));
            OnPropertyChanged(nameof(SelectedAgentAccess));

            foreach (ChatMessage message in Messages)
            {
                message.Role = LocalizationService.Current.T(message.IsUser ? "我" : "AI助手");
                message.RefreshLocalizedLabels();
            }
        }

        private void AIChatbot_Loaded(object sender, RoutedEventArgs e)
        {
            if (!AiFeatureAvailabilityState.Current.IsEnabled)
            {
                Close();
            }
        }

        private void OnAiFeatureAvailabilityChanged(object? sender, EventArgs e)
        {
            if (AiFeatureAvailabilityState.Current.IsEnabled || _resourcesReleased)
            {
                return;
            }

            _windowCancellation.Cancel();
            if (!IsLoaded)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                Close();
                return;
            }

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }

        private async Task GetTitleAsync()
        {
            string titlePrompt = "你是一个专业的文本摘要与标题生成专家。请执行以下步骤：\r\n\r\n1.  **深度理解**：仔细阅读并理解用户输入的文本内容，把握其核心主旨和关键信息。\r\n2.  **提炼摘要**（内部思维）：在脑海中快速提炼出文本的核心摘要。\r\n3.  **拟定标题**：基于摘要，创作一个**精准且高度概括**的标题，标题长度不能太长，尽量在10个字或单词以内。\r\n4.  **输出**：**请仅输出你最终拟定的标题本身**。不要输出任何解释、前言、后记、引号或额外的总结文字，记住你只能输出标题，不要回复好的、嗯等无关字句。";

            const int maxRetryCount = 3;
            Exception? lastException = null;

            for (int retryCount = 0; retryCount < maxRetryCount; retryCount++)
            {
                try
                {
                    using var ai = new AI2(Setting.APIKey, Setting.AIModel, Setting.BasePrompt);
                    using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _windowToken);
                    retryCancellation.CancelAfter(TimeSpan.FromMilliseconds(10000 + retryCount * 3000));
                    string response = await ai
                        .TransformTextAsync(titlePrompt, ContentObject, retryCancellation.Token);
                    _windowToken.ThrowIfCancellationRequested();
                    OnResponse(new ResponseEventArgs
                    {
                        IsComplete = true,
                        Message = response,
                    });
                    return;
                }
                catch (OperationCanceledException) when (_windowToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 捕获异常，记录并继续重试
                    lastException = ex;
                    if (retryCount < maxRetryCount - 1) // 如果不是最后一次重试
                    {
                        // 可选：添加重试延迟，避免频繁请求
                        try
                        {
                            await Task.Delay(
                                1000 * (retryCount + 1),
                                _windowToken); // 递增延迟：1s, 2s, 3s
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }

            // 三次都失败，输出异常信息
            if (_windowToken.IsCancellationRequested)
            {
                return;
            }

            OnResponse(new ResponseEventArgs
            {
                IsComplete = false,
                Message = $"SuperCV网络连接异常，三次重试均失败：{lastException?.Message ?? "未知错误"}",
            });
        }

        private void OnResponse(ResponseEventArgs r)
        {
            if (r.IsComplete)
            {
                ContentTitle = r.Message ?? LocalizationService.Current.T("原始内容");
            }
            else
            {
                ContentTitle = LocalizationService.Current.T("原始内容");
            }
        }
        private ChatMessage _syncResponse = null!;
        public ChatMessage SyncResponse
        {
            get { return _syncResponse; } 
            set { _syncResponse = value; OnPropertyChanged(nameof(SyncResponse)); }     
        }

        private void AddInitialMessages()
        {
            Messages.Add(new ChatMessage
            {
                Role = LocalizationService.Current.T("我"),
                IsUser = true,
                Content = InitialQuery
            });

            SyncResponse = new ChatMessage
            {
                Role = LocalizationService.Current.T("AI助手"),
                IsUser = false,
                ReasoningEffort = SelectedReasoningDepth.Value,
            };
            SyncResponse.BeginStreaming();
            Messages.Add(SyncResponse);
            _ = GenerateAIResponseAsync(InitialQuery, SyncResponse);
        }

        private async Task GenerateAIResponseAsync(
            string query,
            ChatMessage responseMessage,
            string fallbackContent = "",
            string fallbackReasoning = "")
        {
            if (!AiFeatureAvailabilityState.Current.IsEnabled)
            {
                return;
            }

            using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _windowToken);
            _responseCancellation = responseCancellation;
            IsBusy = true;
            responseMessage.IsAgentResponse = true;
            responseMessage.IsReasoningExpanded = true;

            try
            {
                AgentLoopResult result = await RunAgentWithTransientRetryAsync(
                    query,
                    responseMessage,
                    responseCancellation.Token);
                _windowToken.ThrowIfCancellationRequested();
                responseMessage.AgentStepCount = result.Steps;
                responseMessage.CompleteStreaming(result.FinalAnswer, responseMessage.ReasoningContent);
                ScrollToBottom();
            }
            catch (OperationCanceledException) when (_windowToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (responseCancellation.IsCancellationRequested)
            {
                CompleteCanceledResponse(responseMessage, fallbackContent, fallbackReasoning);
            }
            catch (AgentResponseIncompleteException exception)
            {
                if (responseMessage.IsStreaming)
                {
                    string partialOrFallback = string.IsNullOrEmpty(responseMessage.Content)
                        ? fallbackContent
                        : responseMessage.Content;
                    string partialReasoning = string.IsNullOrEmpty(responseMessage.ReasoningContent)
                        ? fallbackReasoning
                        : responseMessage.ReasoningContent;
                    responseMessage.CompleteStreaming(
                        partialOrFallback,
                        partialReasoning,
                        isResponseIncomplete: true);
                }

                if (!_windowToken.IsCancellationRequested)
                {
                    var alert = new AlertDialog(
                        $"AI 回答未完整生成（{exception.FinishReason}），请重新生成。");
                    alert.ShowDialog();
                }
            }
            catch (Exception exception)
            {
                string partialOrFallback = string.IsNullOrEmpty(responseMessage.Content)
                    ? fallbackContent
                    : responseMessage.Content;
                string partialReasoning = string.IsNullOrEmpty(responseMessage.ReasoningContent)
                    ? fallbackReasoning
                    : responseMessage.ReasoningContent;
                if (responseMessage.IsStreaming)
                {
                    responseMessage.CompleteStreaming(
                        partialOrFallback,
                        partialReasoning,
                        isResponseIncomplete: true);
                }

                if (!_windowToken.IsCancellationRequested)
                {
                    if (!IsTransientConnectionFailure(exception))
                    {
                        string alertMessage = exception is AiProtectedContextLimitExceededException
                            ? exception.Message
                            : "AI 请求失败，请检查配置后重试";
                        new AlertDialog(alertMessage)
                        {
                            Owner = this,
                        }.ShowDialog();
                    }
                    else
                    {
                        bool shouldRetry = new AlertDialog(
                            "网络连接异常。是否重新生成这条回答？",
                            "重试")
                        {
                            Owner = this,
                        }.ShowDialog();
                        if (shouldRetry && !_resourcesReleased)
                        {
                            responseMessage.BeginStreaming();
                            _autoScroll = true;
                            await GenerateAIResponseAsync(
                                query,
                                responseMessage,
                                partialOrFallback,
                                partialReasoning);
                        }
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(_responseCancellation, responseCancellation))
                {
                    _responseCancellation = null;
                }

                IsBusy = false;
            }
        }

        private async Task<AgentLoopResult> RunAgentWithTransientRetryAsync(
            string query,
            ChatMessage responseMessage,
            CancellationToken cancellationToken)
        {
            for (int retryCount = 0; ;)
            {
                int receivedModelUpdate = 0;
                try
                {
                    if (_agentConversationNeedsRebuild || retryCount > 0)
                    {
                        // Editing, regeneration, and a failed retry invalidate the retained
                        // semantic summary. A normal new turn keeps it for reuse.
                        _agentLoop.ReplaceConversation(BuildAgentHistory(responseMessage));
                        _agentConversationNeedsRebuild = false;
                    }

                    return await _agentLoop.RunAsync(
                        query,
                        _systemInstruction,
                        SelectedAgentAccess.Value,
                        responseMessage.ReasoningEffort,
                        update => Dispatcher.Invoke(() =>
                        {
                            Interlocked.Exchange(ref receivedModelUpdate, 1);
                            AppendAgentUpdate(responseMessage, update);
                        }),
                        RequestAgentApprovalAsync,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    Volatile.Read(ref receivedModelUpdate) == 0 &&
                    retryCount < MaximumTransientRetryCount &&
                    IsTransientConnectionFailure(exception))
                {
                    retryCount++;
                    _agentConversationNeedsRebuild = true;
                    AppendTransientRetryNotice(responseMessage, retryCount);
                    await Task.Delay(GetTransientRetryDelay(retryCount), cancellationToken);
                }
            }
        }

        private static TimeSpan GetTransientRetryDelay(int retryCount) => retryCount switch
        {
            1 => TimeSpan.FromMilliseconds(800),
            _ => TimeSpan.FromSeconds(2),
        };

        private static bool IsTransientConnectionFailure(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                switch (current)
                {
                    case TimeoutException:
                    case IOException:
                    case OperationCanceledException:
                        return true;
                    case HttpRequestException { StatusCode: null }:
                        return true;
                    case HttpRequestException { StatusCode: { } statusCode } when
                        (int)statusCode is 408 or 429 or >= 500:
                        return true;
                }
            }

            return false;
        }

        private void AppendTransientRetryNotice(ChatMessage responseMessage, int retryCount)
        {
            string separator = string.IsNullOrEmpty(responseMessage.ReasoningContent)
                ? string.Empty
                : Environment.NewLine + Environment.NewLine;
            responseMessage.AppendStreamingReasoning(
                $"{separator}{LocalizationService.Current.T("【连接恢复】")}{Environment.NewLine}" +
                $"{LocalizationService.Current.T("连接中断，正在重试（")}{retryCount}/{MaximumTransientRetryCount}）…");
            ScrollToBottom();
        }

        private static void CompleteCanceledResponse(
            ChatMessage responseMessage,
            string fallbackContent,
            string fallbackReasoning)
        {
            if (!responseMessage.IsStreaming)
            {
                return;
            }

            responseMessage.CompleteStreaming(
                string.IsNullOrEmpty(responseMessage.Content) ? fallbackContent : responseMessage.Content,
                string.IsNullOrEmpty(responseMessage.ReasoningContent)
                    ? fallbackReasoning
                    : responseMessage.ReasoningContent,
                isResponseIncomplete: true);
        }

        private IEnumerable<AiMessage> BuildAgentHistory(ChatMessage responseMessage)
        {
            int responseIndex = Messages.IndexOf(responseMessage);
            int count = Math.Max(0, responseIndex - 1);
            foreach (ChatMessage message in Messages.Take(count))
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    continue;
                }

                yield return new AiMessage(
                    message.IsUser ? AiMessageRole.User : AiMessageRole.Assistant,
                    message.Content);
            }
        }

        private void AppendAgentUpdate(ChatMessage responseMessage, AgentLoopUpdate update)
        {
            responseMessage.AgentStepCount = update.Step;
            switch (update.Kind)
            {
                case AgentLoopUpdateKind.FinalAnswerDelta:
                    responseMessage.AppendStreamingText(update.Text);
                    break;
                case AgentLoopUpdateKind.ReasoningDelta:
                    if (update.StartsSection)
                    {
                        AppendAgentTraceHeading(responseMessage, update.Step, LocalizationService.Current.T("AI 思考"));
                    }

                    responseMessage.AppendStreamingReasoning(update.Text);
                    break;
                case AgentLoopUpdateKind.IntermediateReply:
                    responseMessage.ClearStreamingText();
                    AppendAgentTraceSection(responseMessage, update.Step, "AI", update.Text);
                    break;
                case AgentLoopUpdateKind.ToolRequest:
                    AppendAgentTraceSection(responseMessage, update.Step, "AI", update.Text);
                    break;
                case AgentLoopUpdateKind.ToolActivity:
                    AppendAgentTraceSection(responseMessage, update.Step, LocalizationService.Current.T("工具"), LocalizationService.Current.T(update.Text));
                    break;
                case AgentLoopUpdateKind.ContextCompression:
                    AppendAgentTraceSection(responseMessage, update.Step, LocalizationService.Current.T("上下文压缩"), LocalizationService.Current.T(update.Text));
                    break;
                default:
                    throw new InvalidOperationException("Agent UI 收到了未知的流式更新。 ");
            }

            ScrollToBottom();
        }

        private static void AppendAgentTraceHeading(
            ChatMessage responseMessage,
            int step,
            string prefix)
        {
            string separator = string.IsNullOrEmpty(responseMessage.ReasoningContent)
                ? string.Empty
                : Environment.NewLine + Environment.NewLine;
            responseMessage.AppendStreamingReasoning(
                $"{separator}【{FormatAgentStep(step)} · {prefix}】{Environment.NewLine}");
        }

        private static string FormatAgentStep(int step) =>
            $"{LocalizationService.Current.T("第 ")}{step}{LocalizationService.Current.T(" 轮")}";

        private static void AppendAgentTraceSection(
            ChatMessage responseMessage,
            int step,
            string prefix,
            string text)
        {
            AppendAgentTraceHeading(responseMessage, step, prefix);
            responseMessage.AppendStreamingReasoning(text);
        }

        private Task<bool> RequestAgentApprovalAsync(
            AgentToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool approved = Dispatcher.Invoke(() =>
                new AlertDialog($"Agent 请求执行：{request.Summary}\n\n工具：{request.ToolName}\n是否批准本次操作？")
                    .ShowDialog());
            return Task.FromResult(approved);
        }

        private static AppRuntime GetRuntime()
        {
            if (System.Windows.Application.Current is not App { Runtime: { } runtime })
            {
                throw new InvalidOperationException("SuperCV 运行时尚未初始化。 ");
            }

            return runtime;
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                StopCurrentResponse();
                return;
            }

            _ = SendUserMessageAsync();
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle Enter before the multiline TextBox turns it into a line break.
            if (e.Key == Key.Enter &&
                !e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!IsBusy)
                {
                    _ = SendUserMessageAsync();
                }
            }
        }

        private void StopCurrentResponse()
        {
            if (_responseCancellation is { IsCancellationRequested: false } responseCancellation)
            {
                responseCancellation.Cancel();
            }
        }

        private async Task SendUserMessageAsync()
        {
            if (!AiFeatureAvailabilityState.Current.IsEnabled ||
                IsBusy ||
                string.IsNullOrWhiteSpace(InputText))
            {
                return;
            }

            string userMessage = InputText.Trim();
            InputText = string.Empty;
            ChatMessage? editedMessage = _messageBeingEdited;
            int editedMessageIndex = editedMessage is null
                ? -1
                : Messages.IndexOf(editedMessage);

            if (editedMessageIndex >= 0)
            {
                for (int index = Messages.Count - 1; index > editedMessageIndex; index--)
                {
                    Messages.RemoveAt(index);
                }

                editedMessage!.Content = userMessage;
                editedMessage.Timestamp = DateTime.Now.ToString("HH:mm:ss");
                if (editedMessageIndex == 0)
                {
                    InitialQuery = userMessage;
                }

                _agentConversationNeedsRebuild = true;
            }
            else
            {
                Messages.Add(new ChatMessage
                {
                    Role = LocalizationService.Current.T("我"),
                    IsUser = true,
                    Content = userMessage
                });
            }

            _messageBeingEdited = null;

            SyncResponse = new ChatMessage
            {
                Role = LocalizationService.Current.T("AI助手"),
                IsUser = false,
                ReasoningEffort = SelectedReasoningDepth.Value,
            };
            SyncResponse.BeginStreaming();
            Messages.Add(SyncResponse);
            _autoScroll = true;
            await GenerateAIResponseAsync(userMessage, SyncResponse);
        }

        private void EditQuestion_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || sender is not Button { Tag: ChatMessage responseMessage } || responseMessage.IsUser)
            {
                return;
            }

            int responseIndex = Messages.IndexOf(responseMessage);
            if (responseIndex <= 0 || !Messages[responseIndex - 1].IsUser)
            {
                return;
            }

            _messageBeingEdited = Messages[responseIndex - 1];
            InputText = _messageBeingEdited.Content;
            _autoScroll = true;
            Scroller.ScrollToBottom();

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            }));
        }

        private void ScrollToBottom()
        {
            if (_autoScroll)
            {
                Scroller.ScrollToBottom();
            }
        }

        private async void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var message = button?.Tag as ChatMessage;

            if (button is null || message is null || string.IsNullOrEmpty(message.Content))
            {
                return;
            }

            button.IsEnabled = false;
            bool clipboardWritten = false;
            try
            {
                await MyClipboard.SetTextAsync(message.Content, _windowToken);
                clipboardWritten = true;
                await CVListControl.AddAsync(
                    new Dictionary<TextFormat, string>
                    {
                        [TextFormat.UnicodeText] = message.Content,
                    },
                    _windowToken);
            }
            catch (OperationCanceledException) when (_windowToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Copying an AI response to the clipboard failed: {0}",
                    exception);
                string errorMessage = clipboardWritten
                    ? "已复制到系统剪贴板，但未能新增到 SuperCV，请稍后重试。"
                    : "复制失败，剪贴板正被其他程序占用，请稍后重试。";
                new AlertDialog(errorMessage)
                    .ShowDialog();
            }
            finally
            {
                if (!_resourcesReleased)
                {
                    button.IsEnabled = true;
                }
            }
        }

        private void DeleteMessage_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var message = button?.Tag as ChatMessage;

            if (message != null && Messages.Contains(message))
            {
                Messages.Remove(message);
                _agentConversationNeedsRebuild = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private async void ReGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                return;
            }

            var button = sender as Button;
            var message = button?.Tag as ChatMessage;
            if (message == null)
            {
                return;
            }

            int messageIndex = Messages.IndexOf(message);
            if (messageIndex <= 0)
            {
                return;
            }

            string previousContent = message.Content;
            string previousReasoning = message.ReasoningContent;
            _agentConversationNeedsRebuild = true;
            SyncResponse = message;
            SyncResponse.ReasoningEffort = SelectedReasoningDepth.Value;
            SyncResponse.BeginStreaming();
            _autoScroll = true;
            await GenerateAIResponseAsync(
                Messages[messageIndex - 1].Content,
                SyncResponse,
                previousContent,
                previousReasoning);
        }

        private void ReplyViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {



            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
                e.Handled = true; // 标记为已处理，停止冒泡
            
        }
        private void MdXamlCodeBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                // 排除文档查看器自身的 ScrollViewer，只处理 MdXaml 代码块。
                if (sv.TemplatedParent is FlowDocumentScrollViewer or RichTextBox)
                    return;
                sv.SetResourceReference(Control.BackgroundProperty, "Brush.Control.FillSubtle");
                sv.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
                // 1. 🌟 强行覆盖 MdXaml 底层的硬编码，彻底抹杀横向和纵向滚动条！
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

                // 2. 🌟 空间受限后，为了防止代码被切断看不见，必须开启里面 TextBlock 的自动换行
                if (sv.Content is TextBlock tb)
                {
                    tb.TextWrapping = TextWrapping.Wrap;
                }
                else if (sv.Content is Border border && border.Child is TextBlock innerTb)
                {
                    innerTb.TextWrapping = TextWrapping.Wrap;
                }

            }
        }

        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange < 0) // 向上滚动
            {
                // 检查是否到达底部（考虑一定的容差）
                if (Scroller.VerticalOffset < Scroller.ScrollableHeight)
                {
                    _autoScroll = false;
                }
            }

            // 如果用户滚动到底部，则恢复自动滚动
            if (Math.Abs(Scroller.VerticalOffset - Scroller.ScrollableHeight) < 1)
            {
                _autoScroll = true;
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    public sealed record ReasoningDepthOption(
        AiReasoningEffort Value,
        string Label,
        string Description);
}
