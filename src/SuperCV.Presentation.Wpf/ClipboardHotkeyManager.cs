using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using SuperCV;
using SuperCV.Application.Settings;
using SuperCV.Domain.Settings;
using DomainAppSettings = SuperCV.Domain.Settings.AppSettings;

namespace SuperCV;

internal sealed class ClipboardHotkeyManager : IDisposable
{
    private static readonly Key[] AbsoluteEntryKeys =
    [
        Key.D1,
        Key.D2,
        Key.D3,
        Key.D4,
        Key.D5,
        Key.D6,
        Key.D7,
        Key.D8,
        Key.D9,
        Key.D0,
    ];

    private static readonly Key[] VisibleEntryKeys =
    [
        Key.Q,
        Key.W,
        Key.E,
        Key.R,
        Key.T,
        Key.Y,
    ];

    private readonly SuperCVWindow _owner;
    private readonly SettingsService _settingsService;
    private readonly Dispatcher _dispatcher;
    private readonly ClipboardShortcutNavigator _navigator = new();
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private GlobalInputHook? _hook;
    private GlobalInputHook? _systemClipboardHook;
    private DomainAppSettings _latestSettings;
    private bool _started;
    private bool _suspended;
    private bool _disposed;

    internal ClipboardHotkeyManager(SuperCVWindow owner, SettingsService settingsService)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcher = owner.Dispatcher;
        _latestSettings = settingsService.Snapshot;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _settingsService.Changed += OnSettingsChanged;
        CVListControl.PasteRequested += OnEntryPasteRequested;
        RebuildHook();
    }

    internal void Suspend()
    {
        if (_disposed || _suspended)
        {
            return;
        }

        _suspended = true;
        DisposeHook();
    }

    internal void Resume()
    {
        if (_disposed || !_suspended)
        {
            return;
        }

        _latestSettings = _settingsService.Snapshot;
        _suspended = false;
        TryRebuildHook();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.Changed -= OnSettingsChanged;
        CVListControl.PasteRequested -= OnEntryPasteRequested;
        _lifetime.Cancel();
        DisposeHook();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(
                () => ApplySettings(e.Settings),
                DispatcherPriority.Input);
            return;
        }

        ApplySettings(e.Settings);
    }

    private void ApplySettings(DomainAppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        _latestSettings = settings;
        if (!_suspended)
        {
            TryRebuildHook();
        }
    }

    private void TryRebuildHook()
    {
        try
        {
            RebuildHook();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Failed to apply clipboard hotkeys: {0}", exception);
            if (!_disposed && _owner.IsLoaded)
            {
                new AlertDialog($"快捷键应用失败：{GetInnermostMessage(exception)}")
                {
                    Owner = _owner,
                }.ShowDialog();
            }
        }
    }

    private void RebuildHook()
    {
        var replacement = new GlobalInputHook
        {
            Intercept = _latestSettings.InterceptHotkeys,
        };
        GlobalInputHook? systemClipboardReplacement = null;

        try
        {
            // Win+V is a Windows Shell shortcut, so it must be handled by the low-level
            // hook and suppressed before the Shell can open the system clipboard.
            // It uses its own registration adapter so this dedicated takeover switch
            // remains independent from the general shortcut-interception setting.
            if (_latestSettings.TakeOverWindowsClipboardShortcut)
            {
                systemClipboardReplacement = new GlobalInputHook
                {
                    Intercept = true,
                };
                RegisterShortcut(
                    systemClipboardReplacement,
                    ClipboardShortcutDefaults.OpenSuperCV,
                    _owner.RestoreFromClipboardShortcut);
            }

            for (int index = 0; index < AbsoluteEntryKeys.Length; index++)
            {
                int entryIndex = index;
                replacement.RegisterHotkey(
                    ToModifierKeys(_latestSettings.AbsoluteEntriesModifiers),
                    AbsoluteEntryKeys[index],
                    () => PasteAbsoluteEntry(entryIndex));
            }

            for (int index = 0; index < VisibleEntryKeys.Length; index++)
            {
                int visibleIndex = index;
                replacement.RegisterHotkey(
                    ToModifierKeys(_latestSettings.VisibleEntriesModifiers),
                    VisibleEntryKeys[index],
                    () => PasteVisibleEntry(visibleIndex));
            }

            RegisterShortcut(
                replacement,
                _latestSettings.MoveWindowShortcut,
                () => _owner.MoveToMousePosition());
            RegisterShortcut(
                replacement,
                _latestSettings.PasteOlderShortcut,
                PasteOlderEntry);
            RegisterShortcut(
                replacement,
                _latestSettings.PasteNewerShortcut,
                PasteNewerEntry);
        }
        catch
        {
            systemClipboardReplacement?.Dispose();
            replacement.Dispose();
            throw;
        }

        GlobalInputHook? retired = _hook;
        GlobalInputHook? retiredSystemClipboardHook = _systemClipboardHook;
        _hook = replacement;
        _systemClipboardHook = systemClipboardReplacement;
        retired?.Dispose();
        retiredSystemClipboardHook?.Dispose();
    }

    private void PasteAbsoluteEntry(int entryIndex)
    {
        CVdata[] entries = GetDisplayOrderedEntries();
        Guid[] entryIds = entries.Select(static entry => entry.Id).ToArray();
        int? selectedIndex = _navigator.Select(entryIds, entryIndex);
        if (selectedIndex is int index && index < entries.Length)
        {
            QueuePaste(entries[index]);
        }
    }

    private void PasteVisibleEntry(int visibleIndex)
    {
        if (visibleIndex < 0 || visibleIndex >= CVListControl.ListNow.Count)
        {
            return;
        }

        CVdata entry = CVListControl.ListNow[visibleIndex];
        _navigator.Record(entry.Id);
        QueuePaste(entry);
    }

    private void PasteOlderEntry()
    {
        CVdata[] entries = GetDisplayOrderedEntries();
        Guid[] entryIds = entries.Select(static entry => entry.Id).ToArray();
        int? selectedIndex = _navigator.MoveOlder(entryIds);
        if (selectedIndex is int index && index < entries.Length)
        {
            QueuePaste(entries[index]);
        }
    }

    private void PasteNewerEntry()
    {
        CVdata[] entries = GetDisplayOrderedEntries();
        Guid[] entryIds = entries.Select(static entry => entry.Id).ToArray();
        int? selectedIndex = _navigator.MoveNewer(entryIds);
        if (selectedIndex is int index && index < entries.Length)
        {
            QueuePaste(entries[index]);
        }
    }

    private void QueuePaste(CVdata entry)
    {
        GlobalFocusManager.RecordCurrentForegroundWindow();
        _ = PasteEntryAsync(entry, _lifetime.Token);
    }

    private async Task PasteEntryAsync(CVdata entry, CancellationToken cancellationToken)
    {
        try
        {
            await _pasteGate.WaitAsync(cancellationToken);
            try
            {
                if (!CVListControl.ListAll.Contains(entry))
                {
                    return;
                }

                _ = GlobalFocusManager.RestorePreviousFocus();
                await entry.PasteFromHotkeyAsync(cancellationToken);
            }
            finally
            {
                _pasteGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Clipboard hotkey paste failed: {0}", exception);
            if (!_disposed)
            {
                new AlertDialog($"快捷键粘贴失败：{GetInnermostMessage(exception)}")
                {
                    Owner = _owner,
                }.ShowDialog();
            }
        }
    }

    private void OnEntryPasteRequested(Guid entryId)
    {
        _navigator.Record(entryId);
    }

    private static CVdata[] GetDisplayOrderedEntries() =>
        CVListControl.ListAll
            .OrderByDescending(static entry => entry.TopMost)
            .ToArray();

    private static void RegisterShortcut(
        GlobalInputHook hook,
        ShortcutGesture gesture,
        Action callback)
    {
        ModifierKeys modifiers = ToModifierKeys(gesture.Modifiers);
        if (gesture.VirtualKey is int virtualKey)
        {
            Key key = KeyInterop.KeyFromVirtualKey(virtualKey);
            if (key == Key.None)
            {
                throw new InvalidOperationException(
                    $"快捷键包含不受支持的按键代码：{virtualKey}。");
            }

            hook.RegisterHotkey(modifiers, key, callback);
            return;
        }

        hook.RegisterHotkey(
            modifiers,
            gesture.MouseButton switch
            {
                ShortcutMouseButton.Left => MouseButton.Left,
                ShortcutMouseButton.Right => MouseButton.Right,
                ShortcutMouseButton.Middle => MouseButton.Middle,
                ShortcutMouseButton.XButton1 => MouseButton.XButton1,
                ShortcutMouseButton.XButton2 => MouseButton.XButton2,
                _ => throw new ArgumentOutOfRangeException(nameof(gesture)),
            },
            callback);
    }

    private static ModifierKeys ToModifierKeys(ShortcutModifiers modifiers)
    {
        ModifierKeys result = ModifierKeys.None;
        if ((modifiers & ShortcutModifiers.Alt) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((modifiers & ShortcutModifiers.Control) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((modifiers & ShortcutModifiers.Shift) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((modifiers & ShortcutModifiers.Windows) != 0)
        {
            result |= ModifierKeys.Windows;
        }

        return result;
    }

    private void DisposeHook()
    {
        GlobalInputHook? hook = _hook;
        GlobalInputHook? systemClipboardHook = _systemClipboardHook;
        _hook = null;
        _systemClipboardHook = null;
        hook?.Dispose();
        systemClipboardHook?.Dispose();
    }

    private static string GetInnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception.Message;
    }
}
