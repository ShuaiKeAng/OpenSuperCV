using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using System.IO;
using SuperCV.Application;
using DomainInstruction = global::SuperCV.Domain.Instructions.CustomInstruction;

namespace SuperCV;

/// <summary>
/// Presents the shared instruction catalog in one or more CV popups. Each popup owns its
/// content, response sink, cancellation token and request generation so responses cannot
/// be delivered to another window.
/// </summary>
public static class CustomInstructionsManager
{
    private static readonly List<WeakReference<InstructionSession>> Sessions = new();
    private static readonly Dictionary<Guid, InstructionDocumentMonitor> DocumentMonitors = [];
    private static List<DomainInstruction> _instructions = new();
    private static WeakReference<InstructionSession>? _activeSession;
    private static bool _loaded;

    public sealed class ResponseEventArgs : EventArgs
    {
        public bool IsComplete { get; set; }

        public string? Message { get; set; }

        public bool IsError { get; set; }
    }

    /// <summary>
    /// Compatibility surface for the existing CV code. Subscription is scoped to the panel
    /// passed to the most recent <see cref="Init"/> call, never to the whole process.
    /// </summary>
    public static event EventHandler<ResponseEventArgs>? Response
    {
        add
        {
            if (TryGetActiveSession(out InstructionSession session))
            {
                session.Response -= value;
                session.Response += value;
            }
        }
        remove
        {
            if (TryGetActiveSession(out InstructionSession session))
            {
                session.Response -= value;
            }
        }
    }

    public static void Init(StackPanel stackPanel, string? _content = null)
    {
        ArgumentNullException.ThrowIfNull(stackPanel);
        InstructionSession? session = FindSession(stackPanel);
        if (session is null)
        {
            session = new InstructionSession(stackPanel);
            Sessions.Add(new WeakReference<InstructionSession>(session));
        }

        session.SetContent(_content ?? string.Empty);
        _activeSession = new WeakReference<InstructionSession>(session);
        try
        {
            EnsureLoaded();
            Render(session);
        }
        catch (Exception exception)
        {
            session.Panel.Children.Clear();
            session.Panel.Tag = "0";
            new AlertDialog($"读取指令失败: {exception.Message}").ShowDialog();
        }
    }

    public static async void AddInstruction(string label, string prompt)
    {
        try
        {
            await AddInstructionCoreAsync(label, prompt).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            new AlertDialog($"保存失败: {exception.Message}").ShowDialog();
        }
    }

    private static async Task AddInstructionCoreAsync(string label, string prompt)
    {
        EnsureLoaded();
        string normalizedLabel = (label ?? string.Empty).Replace(" ", "_");
        if (_instructions.Any(item => item.Label.Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase)))
        {
            new AlertDialog("已有重名标签，请重新设置").ShowDialog();
            return;
        }

        if (!FileNameValidator.IsValidFileName(normalizedLabel))
        {
            new AlertDialog("标签名含无效字符，请重新设置").ShowDialog();
            return;
        }

        var instruction = new DomainInstruction(
            Guid.NewGuid(),
            normalizedLabel,
            prompt ?? string.Empty,
            DateTimeOffset.UtcNow);
        if (await GetRuntime().Instructions.AddAsync(instruction).ConfigureAwait(true))
        {
            RefreshInstructions();
            RenderAllSessions();
        }
    }

    public static Task FlushAsync() =>
        System.Windows.Application.Current is App { Runtime: { } runtime }
            ? runtime.Instructions.FlushAsync()
            : Task.CompletedTask;

    internal static void RefreshForLanguageChange()
    {
        if (!_loaded)
        {
            return;
        }

        RefreshInstructions();
        RenderAllSessions();
    }

    public static void Detach(StackPanel stackPanel)
    {
        ArgumentNullException.ThrowIfNull(stackPanel);
        for (int index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!Sessions[index].TryGetTarget(out InstructionSession? session))
            {
                Sessions.RemoveAt(index);
                continue;
            }

            if (!ReferenceEquals(session.Panel, stackPanel))
            {
                continue;
            }

            session.Detach();
            Sessions.RemoveAt(index);
            if (_activeSession is not null
                && _activeSession.TryGetTarget(out InstructionSession? active)
                && ReferenceEquals(active, session))
            {
                _activeSession = null;
            }

            return;
        }
    }

    private static async void Execute(
        InstructionSession session,
        Guid instructionId)
    {
        if (!AiFeatureAvailabilityState.Current.IsEnabled)
        {
            return;
        }

        DomainInstruction? instruction = _instructions.FirstOrDefault(item => item.Id == instructionId);
        if (instruction is null)
        {
            return;
        }

        (long generation, CancellationToken cancellationToken) = session.BeginRequest();
        session.Publish(new ResponseEventArgs
        {
            IsComplete = false,
            Message = "开始处理请求...",
        });

        try
        {
            using var ai = new AI2(
                apiKey: null,
                provider: Setting.AIModel,
                basePrompt: Setting.BasePrompt);
            string response = await AiRequestRetryPolicy.ExecuteAsync(
                    token => ai.TransformTextAsync(instruction.Prompt, session.Content, token),
                    cancellationToken)
                .ConfigureAwait(true);

            if (session.IsCurrent(generation))
            {
                session.Publish(new ResponseEventArgs
                {
                    IsComplete = true,
                    Message = response,
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (session.IsCurrent(generation))
            {
                session.Publish(new ResponseEventArgs
                {
                    IsComplete = true,
                    IsError = true,
                    Message = AiRequestRetryPolicy.DescribeFailure(exception, "AI 文本处理"),
                });
            }
        }
    }

    private static void Render(InstructionSession session)
    {
        session.Panel.Children.Clear();
        foreach (DomainInstruction instruction in _instructions)
        {
            session.Panel.Children.Add(CreateInstructionRow(session, instruction));
        }

        session.Panel.Tag = _instructions.Count.ToString();
    }

    private static Grid CreateInstructionRow(
        InstructionSession session,
        DomainInstruction instruction)
    {
        var grid = new Grid();
        var mainButton = new Button
        {
            Style = (Style)System.Windows.Application.Current.TryFindResource("moreButtonStyle"),
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock
            {
                Text = instruction.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 110,
            },
            Height = 26,
            Background = (Brush)System.Windows.Application.Current.TryFindResource("Brush.Surface.Raised"),
            FontSize = 14,
            Foreground = (Brush)System.Windows.Application.Current.TryFindResource("Brush.Text.Primary"),
        };

        var closeButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)System.Windows.Application.Current.TryFindResource("moreButtonDeleteStyle"),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Height = 20,
            Width = 20,
            Margin = new Thickness(3),
            Background = Brushes.Transparent,
        };

        var viewbox = new Viewbox { Width = 10, Height = 10 };
        object closeResource = System.Windows.Application.Current.TryFindResource("Close1");
        if (closeResource is FrameworkElement closeElement)
        {
            viewbox.Child = closeElement;
        }
        else if (closeResource is Brush closeBrush)
        {
            viewbox.Child = new System.Windows.Shapes.Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = closeBrush,
            };
        }

        closeButton.Content = viewbox;
        mainButton.Click += (_, _) => Execute(session, instruction.Id);
        mainButton.MouseRightButtonUp += async (_, _) =>
            await RunUiOperationAsync(() => EditAsync(instruction.Id)).ConfigureAwait(true);
        closeButton.Click += async (_, _) =>
            await RunUiOperationAsync(() => DeleteAsync(instruction.Id)).ConfigureAwait(true);

        grid.Children.Add(mainButton);
        grid.Children.Add(closeButton);
        return grid;
    }

    private static async Task EditAsync(Guid instructionId)
    {
        DomainInstruction? instruction = _instructions.FirstOrDefault(item => item.Id == instructionId);
        if (instruction is null)
        {
            return;
        }

        var dialog = new TextBox2Dialog(
            title: "AI文本处理设置",
            tip1: "请输入标签名",
            tip2: "请输入提示词",
            label: instruction.Label,
            prompt: instruction.Prompt,
            openPromptDocumentAsync: () => OpenInstructionDocumentAsync(instruction.Id));
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string normalizedLabel = (dialog.LabelName ?? string.Empty).Replace(" ", "_");
        if (_instructions.Any(item =>
                item.Id != instructionId
                && item.Label.Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase)))
        {
            new AlertDialog("已有重名标签，请重新设置").ShowDialog();
            return;
        }

        if (!FileNameValidator.IsValidFileName(normalizedLabel))
        {
            new AlertDialog("标签名含无效字符，请重新设置").ShowDialog();
            return;
        }

        var updated = instruction with
        {
            Label = normalizedLabel,
            Prompt = dialog.Prompt ?? string.Empty,
        };
        if (await GetRuntime().Instructions.UpdateAsync(updated).ConfigureAwait(true))
        {
            StopDocumentMonitor(instructionId);
            RefreshInstructions();
            RenderAllSessions();
        }
    }

    private static async Task DeleteAsync(Guid instructionId)
    {
        DomainInstruction? instruction = _instructions.FirstOrDefault(item => item.Id == instructionId);
        if (instruction is null)
        {
            return;
        }

        var confirmation = new AlertDialog($"确定删除\"{instruction.Label}\"？");
        if (!confirmation.ShowDialog())
        {
            return;
        }

        AppRuntime runtime = GetRuntime();
        if (FirstUseDefaults.IsPresetInstructionId(instructionId))
        {
            // Persist the user's deletion before removing the document.  The document catalog is
            // intentionally allowed to be empty, so its absence alone cannot represent this choice.
            await runtime.Settings.UpdateAndPersistAsync(
                    current => current.DeletedInstructionPresetIds.Contains(instructionId)
                        ? current
                        : current with
                        {
                            DeletedInstructionPresetIds =
                            [.. current.DeletedInstructionPresetIds, instructionId],
                        })
                .ConfigureAwait(true);
        }

        if (await runtime.Instructions.RemoveAsync(instructionId).ConfigureAwait(true))
        {
            await runtime.Instructions.FlushAsync().ConfigureAwait(true);
            StopDocumentMonitor(instructionId);
            RefreshInstructions();
            RenderAllSessions();
        }
    }

    private static async Task OpenInstructionDocumentAsync(Guid instructionId)
    {
        AppRuntime runtime = GetRuntime();
        await runtime.Instructions.FlushAsync().ConfigureAwait(true);
        string? path = runtime.Instructions.GetDocumentPath(instructionId);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException("找不到对应的 Markdown 指令文件。");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到对应的 Markdown 指令文件。", path);
        }

        StartDocumentMonitor(instructionId, path);
        try
        {
            var startInfo = new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(path);
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("无法启动记事本。");
            }
        }
        catch
        {
            StopDocumentMonitor(instructionId);
            throw;
        }
    }

    private static void StartDocumentMonitor(Guid instructionId, string path)
    {
        StopDocumentMonitor(instructionId);
        DocumentMonitors[instructionId] = new InstructionDocumentMonitor(
            path,
            cancellationToken => RefreshExternalInstructionAsync(
                instructionId,
                cancellationToken));
    }

    private static void StopDocumentMonitor(Guid instructionId)
    {
        if (DocumentMonitors.Remove(instructionId, out InstructionDocumentMonitor? monitor))
        {
            monitor.Dispose();
        }
    }

    private static async Task RefreshExternalInstructionAsync(
        Guid instructionId,
        CancellationToken cancellationToken)
    {
        if (System.Windows.Application.Current is not App { Runtime: { } runtime } application)
        {
            return;
        }

        bool changed = await runtime.Instructions
            .RefreshDocumentAsync(instructionId, cancellationToken)
            .ConfigureAwait(false);
        if (!changed)
        {
            return;
        }

        await application.Dispatcher.InvokeAsync(() =>
        {
            RefreshInstructions();
            RenderAllSessions();
        }).Task.ConfigureAwait(false);
    }

    private static async Task RunUiOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            new AlertDialog($"操作失败: {exception.Message}").ShowDialog();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        RefreshInstructions();
        _loaded = true;
    }

    private static void RefreshInstructions() =>
        _instructions = GetRuntime().Instructions.Snapshot.ToList();

    private static void RenderAllSessions()
    {
        for (int index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!Sessions[index].TryGetTarget(out InstructionSession? session))
            {
                Sessions.RemoveAt(index);
                continue;
            }

            Render(session);
        }
    }

    private static InstructionSession? FindSession(StackPanel panel)
    {
        for (int index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!Sessions[index].TryGetTarget(out InstructionSession? session))
            {
                Sessions.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(session.Panel, panel))
            {
                return session;
            }
        }

        return null;
    }

    private static bool TryGetActiveSession(out InstructionSession session)
    {
        if (_activeSession is not null
            && _activeSession.TryGetTarget(out InstructionSession? active)
            && active is not null)
        {
            session = active;
            return true;
        }

        _activeSession = null;
        session = null!;
        return false;
    }

    private static AppRuntime GetRuntime()
    {
        if (System.Windows.Application.Current is not App { Runtime: { } runtime })
        {
            throw new InvalidOperationException("SuperCV 运行时尚未初始化。");
        }

        return runtime;
    }

    private sealed class InstructionSession
    {
        private CancellationTokenSource? _requestCancellation;
        private long _generation;

        internal InstructionSession(StackPanel panel)
        {
            Panel = panel;
        }

        internal StackPanel Panel { get; }

        internal string Content { get; private set; } = string.Empty;

        internal event EventHandler<ResponseEventArgs>? Response;

        internal void SetContent(string content)
        {
            Content = content;
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            _generation++;
        }

        internal (long Generation, CancellationToken CancellationToken) BeginRequest()
        {
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = new CancellationTokenSource();
            return (++_generation, _requestCancellation.Token);
        }

        internal bool IsCurrent(long generation) => generation == _generation;

        internal void Publish(ResponseEventArgs response) => Response?.Invoke(this, response);

        internal void Detach()
        {
            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            _generation++;
            Response = null;
            Content = string.Empty;
            Panel.Children.Clear();
            Panel.Tag = "0";
        }
    }
}
