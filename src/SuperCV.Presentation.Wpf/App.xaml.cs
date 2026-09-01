using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SuperCV.Infrastructure.Windows;

namespace SuperCV;

public partial class App : System.Windows.Application
{
    private const long ScrollCleanupHeapThresholdBytes = 48L * 1024 * 1024;
    private WindowsSingleInstanceLease? _singleInstanceLease;
    private System.Windows.Threading.DispatcherTimer? _historyBodyEvictionTimer;
    private System.Windows.Threading.DispatcherTimer? _scrollMemoryCleanupTimer;
    private bool _restoreRunningInstancePending;
    private bool _restartAfterImportRequested;

    internal AppRuntime Runtime { get; private set; } = null!;

    internal void RestartAfterImport()
    {
        _restartAfterImportRequested = true;
        Shutdown(0);
    }

    internal void RestartAfterDataRootChange()
    {
        _restartAfterImportRequested = true;
        Shutdown(0);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                Keyboard.PreviewGotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(SuppressNonTextInputKeyboardFocus),
                handledEventsToo: true);
            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(LocalizeElementOnLoaded),
                handledEventsToo: true);
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AnimatePopupWindowOnLoaded),
                handledEventsToo: true);

            string dataRoot = AppRuntime.ResolveDataRoot();
            DataTransferService.ApplyPendingImport(dataRoot);
            if (WindowsInfrastructure.HasWorkspaceCatalog(dataRoot))
            {
                _ = DataRootLocationStore.ForCurrentUser()
                    .TryDeleteScheduledSourceData(dataRoot);
            }
            _singleInstanceLease = WindowsSingleInstanceLease.TryAcquire(dataRoot);
            if (_singleInstanceLease is null)
            {
                WindowsSingleInstanceLease.SignalActivationRequest(dataRoot);
                Shutdown(0);
                return;
            }

            _singleInstanceLease.StartActivationListener(
                () => Dispatcher.BeginInvoke(RestoreRunningInstance));
            Runtime = AppRuntime.CreateAsync(dataRoot).AsTask().GetAwaiter().GetResult();
            ThemeService.Initialize(dataRoot);
            if (ThemeService.ConsumeSelectionResetRequirement() &&
                !string.Equals(Runtime.Settings.Snapshot.ThemeId, ThemeCatalog.DefaultThemeId, StringComparison.OrdinalIgnoreCase))
            {
                Runtime.Settings.Update(settings => settings with { ThemeId = ThemeCatalog.DefaultThemeId });
            }
            Runtime.History.Changed += ScheduleHistoryBodyEviction;
            CVListControl.ScrollStarted += ScheduleScrollMemoryCleanup;
            CompactLargeObjectHeapBeforeFirstWindow();
            Setting.Initialize(Runtime.Settings, Runtime.StartupRegistration);
            LocalizationService.Current.Initialize(Runtime.Settings, Dispatcher);
            MainSurfaceAppearanceState.Current.Initialize(Runtime.Settings, Dispatcher);
            TextEntryBackgroundAppearanceState.Current.Initialize(Runtime.Settings, Dispatcher);
            AiFeatureAvailabilityState.Current.Initialize(Runtime.Settings, Dispatcher);
            MyClipboard.Initialize(Runtime.Clipboard);
            GlobalFocusManager.InitializeService(Runtime.Focus);
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            _singleInstanceLease?.Dispose();
            _singleInstanceLease = null;
            Debug.WriteLine(exception);
            MessageBox.Show(
                $"SuperCV 启动失败：{GetInnermostMessage(exception)}",
                "SuperCV",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static void SuppressNonTextInputKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox or RichTextBox or PasswordBox)
        {
            return;
        }

        e.Handled = true;
    }

    private static void CompactLargeObjectHeapBeforeFirstWindow()
    {
        // Legacy JSON migration can temporarily allocate large strings. This is intentionally
        // before any application window is shown, so the one-off collection cannot disturb
        // scrolling or popup animation cadence.
        CompactLargeObjectHeap();
    }

    private static void CompactLargeObjectHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void ScheduleHistoryBodyEviction(object? sender, global::SuperCV.Application.History.HistoryChangedEventArgs e)
    {
        if (!e.Snapshot.Entries.Any(entry => !entry.Payload.IsDeferred))
        {
            return;
        }

        _historyBodyEvictionTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _historyBodyEvictionTimer.Stop();
        _historyBodyEvictionTimer.Tick -= HistoryBodyEvictionTimer_Tick;
        _historyBodyEvictionTimer.Tick += HistoryBodyEvictionTimer_Tick;
        _historyBodyEvictionTimer.Start();
    }

    private async void HistoryBodyEvictionTimer_Tick(object? sender, EventArgs e)
    {
        _historyBodyEvictionTimer?.Stop();
        try
        {
            await Runtime.History.ReleasePersistedBodiesAsync();
            // This happens only after two seconds without a history change, at ApplicationIdle.
            // It releases temporary migration/edit strings without running a blocking compacting
            // collection during user-driven scrolling or animations.
            CompactLargeObjectHeap();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"History body eviction failed: {exception}");
        }
    }

    private void ScheduleScrollMemoryCleanup(object? sender, EventArgs e)
    {
        _scrollMemoryCleanupTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _scrollMemoryCleanupTimer.Stop();
        _scrollMemoryCleanupTimer.Tick -= ScrollMemoryCleanupTimer_Tick;
        _scrollMemoryCleanupTimer.Tick += ScrollMemoryCleanupTimer_Tick;
        _scrollMemoryCleanupTimer.Start();
    }

    private void ScrollMemoryCleanupTimer_Tick(object? sender, EventArgs e)
    {
        _scrollMemoryCleanupTimer?.Stop();
        // Never interrupt editing, menus, or the exit/enter animation sequence. A subsequent
        // idle tick will retry while the user keeps that surface open.
        if (CVListControl.HasOpenPopup())
        {
            ScheduleScrollMemoryCleanup(sender, e);
            return;
        }

        // Small histories do not justify a full compacting collection. Long items crossed this
        // threshold during scrolling and otherwise leave reclaimed strings in LOH segments.
        if (GC.GetGCMemoryInfo().HeapSizeBytes >= ScrollCleanupHeapThresholdBytes)
        {
            CompactLargeObjectHeap();
        }
    }

    private static void AnimatePopupWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            LocalizationService.Current.Localize(window);
            PopupWindowAnimator.TryBeginFadeIn(
                window,
                Setting.EnableAdvancedAnimation);
        }
    }

    private static void LocalizeElementOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            LocalizationService.Current.LocalizeElement(element);
        }
    }

    private void RestoreRunningInstance()
    {
        SuperCVWindow? mainWindow = MainWindow as SuperCVWindow
            ?? Windows.OfType<SuperCVWindow>().FirstOrDefault();
        if (mainWindow is null)
        {
            _restoreRunningInstancePending = true;
            return;
        }

        _restoreRunningInstancePending = false;
        mainWindow.RestoreFromTray();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_restoreRunningInstancePending)
        {
            RestoreRunningInstance();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var failures = new List<Exception>();
        try
        {
            if (Runtime is not null)
            {
                Runtime.History.Changed -= ScheduleHistoryBodyEviction;
            }
            _historyBodyEvictionTimer?.Stop();
            _historyBodyEvictionTimer = null;
            CVListControl.ScrollStarted -= ScheduleScrollMemoryCleanup;
            _scrollMemoryCleanupTimer?.Stop();
            _scrollMemoryCleanupTimer = null;
            CustomInstructionsManager.FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            AiFeatureAvailabilityState.Current.Dispose();
            TextEntryBackgroundAppearanceState.Current.Dispose();
            MainSurfaceAppearanceState.Current.Dispose();
            LocalizationService.Current.Dispose();
            Runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            _singleInstanceLease?.Dispose();
            _singleInstanceLease = null;
            if (_restartAfterImportRequested && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                });
            }
            foreach (Exception failure in failures)
            {
                Debug.WriteLine(failure);
            }

            base.OnExit(e);
        }
    }

    private static string GetInnermostMessage(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return string.IsNullOrWhiteSpace(current.Message)
            ? "未知错误"
            : current.Message;
    }
}
