using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MdXaml;
using SuperCV.Domain.AI;

namespace SuperCV
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private readonly Markdown _markdown;
        private string _role = string.Empty;
        private string _content = string.Empty;
        private string _timestamp = string.Empty;
        private string _reasoningContent = string.Empty;
        private bool _isUser;
        private bool _isStreaming;
        private bool _isReasoningExpanded;
        private bool _isAgentResponse;
        private bool _isResponseIncomplete;
        private int _agentStepCount;
        private AiReasoningEffort _reasoningEffort;
        private FlowDocument? _formattedContent;

        public ChatMessage()
        {
            _markdown = CreateMarkdownRenderer();
            Timestamp = DateTime.Now.ToString("HH:mm:ss");
        }

        public string Role
        {
            get => _role;
            set
            {
                _role = value ?? string.Empty;
                OnPropertyChanged(nameof(Role));
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                _content = value ?? string.Empty;
                OnPropertyChanged(nameof(Content));
                if (!IsUser && !IsStreaming)
                {
                    RenderMarkdown();
                }
            }
        }

        public string Timestamp
        {
            get => _timestamp;
            set
            {
                _timestamp = value ?? string.Empty;
                OnPropertyChanged(nameof(Timestamp));
            }
        }

        public string ReasoningContent
        {
            get => _reasoningContent;
            set
            {
                _reasoningContent = value ?? string.Empty;
                OnPropertyChanged(nameof(ReasoningContent));
            }
        }

        public AiReasoningEffort ReasoningEffort
        {
            get => _reasoningEffort;
            set
            {
                _reasoningEffort = Enum.IsDefined(value) ? value : AiReasoningEffort.Off;
                OnPropertyChanged(nameof(ReasoningEffort));
                OnPropertyChanged(nameof(ReasoningTitle));
            }
        }

        public string ReasoningTitle => IsAgentResponse
            ? FormatAgentReasoningTitle()
            : LocalizationService.Current.T(ReasoningEffort switch
            {
                AiReasoningEffort.Low => IsStreaming ? "思考过程 · 较弱 · 生成中" : "思考过程 · 较弱",
                AiReasoningEffort.High => IsStreaming ? "思考过程 · 较高 · 生成中" : "思考过程 · 较高",
                AiReasoningEffort.Max => IsStreaming ? "思考过程 · 最高 · 生成中" : "思考过程 · 最高",
                _ => IsStreaming ? "思考过程 · 生成中" : "思考过程",
            });

        public string CopyActionText => LocalizationService.Current.T("复制");

        public string EditQuestionActionText => LocalizationService.Current.T("编辑问题");

        public string EditQuestionToolTip => LocalizationService.Current.T("编辑这条回复对应的问题");

        public string RegenerateActionText => LocalizationService.Current.T("重新生成");

        public bool IsAgentResponse
        {
            get => _isAgentResponse;
            set
            {
                if (_isAgentResponse == value)
                {
                    return;
                }

                _isAgentResponse = value;
                OnPropertyChanged(nameof(IsAgentResponse));
                OnPropertyChanged(nameof(ReasoningTitle));
            }
        }

        public bool IsResponseIncomplete
        {
            get => _isResponseIncomplete;
            private set
            {
                if (_isResponseIncomplete == value)
                {
                    return;
                }

                _isResponseIncomplete = value;
                OnPropertyChanged(nameof(IsResponseIncomplete));
                OnPropertyChanged(nameof(ReasoningTitle));
            }
        }

        public int AgentStepCount
        {
            get => _agentStepCount;
            set
            {
                int normalized = Math.Max(0, value);
                if (_agentStepCount == normalized)
                {
                    return;
                }

                _agentStepCount = normalized;
                OnPropertyChanged(nameof(AgentStepCount));
                OnPropertyChanged(nameof(ReasoningTitle));
            }
        }

        public bool IsReasoningExpanded
        {
            get => _isReasoningExpanded;
            set
            {
                if (_isReasoningExpanded == value)
                {
                    return;
                }

                _isReasoningExpanded = value;
                OnPropertyChanged(nameof(IsReasoningExpanded));
            }
        }

        public bool IsUser
        {
            get => _isUser;
            set
            {
                _isUser = value;
                OnPropertyChanged(nameof(IsUser));
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            private set
            {
                if (_isStreaming == value)
                {
                    return;
                }

                _isStreaming = value;
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(ReasoningTitle));
            }
        }

        public FlowDocument? FormattedContent
        {
            get => _formattedContent;
            private set
            {
                _formattedContent = value;
                OnPropertyChanged(nameof(FormattedContent));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshLocalizedLabels()
        {
            OnPropertyChanged(nameof(ReasoningTitle));
            OnPropertyChanged(nameof(CopyActionText));
            OnPropertyChanged(nameof(EditQuestionActionText));
            OnPropertyChanged(nameof(EditQuestionToolTip));
            OnPropertyChanged(nameof(RegenerateActionText));
            RefreshAgentTraceHeadings();
        }

        public void BeginStreaming()
        {
            _content = string.Empty;
            _reasoningContent = string.Empty;
            FormattedContent = null;
            IsReasoningExpanded = false;
            IsResponseIncomplete = false;
            IsStreaming = true;
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(ReasoningContent));
        }

        public void AppendStreamingText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _content += text;
            OnPropertyChanged(nameof(Content));
        }

        public void ClearStreamingText()
        {
            if (_content.Length == 0)
            {
                return;
            }

            _content = string.Empty;
            OnPropertyChanged(nameof(Content));
        }

        public void AppendStreamingReasoning(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _reasoningContent += text;
            OnPropertyChanged(nameof(ReasoningContent));
        }

        public void CompleteStreaming(
            string content,
            string reasoningContent = "",
            bool isResponseIncomplete = false)
        {
            _content = content ?? string.Empty;
            _reasoningContent = reasoningContent ?? string.Empty;
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(ReasoningContent));
            RenderMarkdown();
            IsResponseIncomplete = isResponseIncomplete;
            IsStreaming = false;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static Markdown CreateMarkdownRenderer()
        {
            var renderer = new Markdown();

            var documentStyle = new Style(typeof(FlowDocument));
            documentStyle.Setters.Add(new Setter(TextElement.FontSizeProperty, 12.0));
            documentStyle.Setters.Add(new Setter(
                TextElement.FontFamilyProperty,
                new FontFamily("Microsoft YaHei")));
            documentStyle.Setters.Add(new Setter(Block.LineHeightProperty, 16.0));
            documentStyle.Setters.Add(new Setter(
                FlowDocument.PagePaddingProperty,
                new Thickness(0)));
            renderer.DocumentStyle = documentStyle;

            var tableStyle = new Style(typeof(Table));
            tableStyle.Setters.Add(new Setter(TextElement.FontSizeProperty, 12.0));
            tableStyle.Setters.Add(new Setter(
                TextElement.FontFamilyProperty,
                new FontFamily("Microsoft YaHei")));
            tableStyle.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(1)));
            renderer.TableStyle = tableStyle;

            var codeStyle = new Style(typeof(Run));
            codeStyle.Setters.Add(new Setter(TextElement.FontSizeProperty, 12.0));
            codeStyle.Setters.Add(new Setter(
                TextElement.FontFamilyProperty,
                new FontFamily("Consolas")));
            codeStyle.Setters.Add(new Setter(
                TextElement.ForegroundProperty,
                new DynamicResourceExtension("Brush.Text.Muted")));
            codeStyle.Setters.Add(new Setter(
                TextElement.BackgroundProperty,
                new DynamicResourceExtension("CodeInlineBackgroundBrush")));
            renderer.CodeStyle = codeStyle;

            var codeBlockStyle = new Style(typeof(BlockUIContainer));
            codeBlockStyle.Setters.Add(new Setter(TextElement.FontSizeProperty, 12.0));
            codeBlockStyle.Setters.Add(new Setter(
                TextElement.FontFamilyProperty,
                new FontFamily("Consolas")));
            codeBlockStyle.Setters.Add(new Setter(
                TextElement.ForegroundProperty,
                new DynamicResourceExtension("Brush.Text.Muted")));
            codeBlockStyle.Setters.Add(new Setter(Block.LineHeightProperty, double.NaN));
            codeBlockStyle.Setters.Add(new Setter(Block.BackgroundProperty, Brushes.Transparent));
            codeBlockStyle.Setters.Add(new Setter(
                Block.BorderBrushProperty,
                new DynamicResourceExtension("Brush.Border.Default")));
            codeBlockStyle.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(1)));
            codeBlockStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(1)));
            codeBlockStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 5, 0, 5)));
            renderer.CodeBlockStyle = codeBlockStyle;

            return renderer;
        }

        private string FormatAgentReasoningTitle()
        {
            string activity = LocalizationService.Current.T("Agent 执行过程");
            string step = FormatAgentStep(IsStreaming ? Math.Max(1, AgentStepCount) : AgentStepCount);
            return IsResponseIncomplete
                ? $"{activity} · {step} · {LocalizationService.Current.T("输出未完成")}"
                : $"{activity} · {step}";
        }

        private static string FormatAgentStep(int step) =>
            $"{LocalizationService.Current.T("第 ")}{step}{LocalizationService.Current.T(" 轮")}";

        private void RefreshAgentTraceHeadings()
        {
            if (!IsAgentResponse || string.IsNullOrEmpty(_reasoningContent))
            {
                return;
            }

            string traceWithLocalizedHeadings = Regex.Replace(
                NormalizeLocalizedAgentTraceText(_reasoningContent),
                @"【(?:第\s*|Step\s*)(\d+)(?:\s*轮)?\s*·\s*([^】]+)】",
                match =>
                {
                    string sourcePrefix = match.Groups[2].Value switch
                    {
                        "AI reasoning" => "AI 思考",
                        "Tool" => "工具",
                        "Context compression" => "上下文压缩",
                        string prefix => prefix,
                    };
                    return $"【{FormatAgentStep(int.Parse(match.Groups[1].Value))} · {LocalizationService.Current.T(sourcePrefix)}】";
                });
            string localized = LocalizationService.Current.T(traceWithLocalizedHeadings);

            if (!string.Equals(_reasoningContent, localized, StringComparison.Ordinal))
            {
                _reasoningContent = localized;
                OnPropertyChanged(nameof(ReasoningContent));
            }
        }

        private static string NormalizeLocalizedAgentTraceText(string text) => text
            .Replace("[Connection recovery]", "【连接恢复】", StringComparison.Ordinal)
            .Replace("Connection interrupted; retrying (", "连接中断，正在重试（", StringComparison.Ordinal);

        private void RenderMarkdown()
        {
            try
            {
                FormattedContent = _markdown.Transform(_content);
            }
            catch
            {
                FormattedContent = new FlowDocument(new Paragraph(new Run(_content)));
            }
        }
    }

    public class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserMessageTemplate { get; set; }

        public DataTemplate? AIMessageTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage message)
            {
                return message.IsUser ? UserMessageTemplate : AIMessageTemplate;
            }

            return base.SelectTemplate(item, container);
        }
    }
}
