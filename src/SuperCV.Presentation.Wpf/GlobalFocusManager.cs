using System.Windows;
using SuperCV.Application.Ports;

public static class GlobalFocusManager
{
    private static readonly object Gate = new();
    private static IFocusService? _service;

    internal static void InitializeService(IFocusService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (Gate)
        {
            _service ??= service;
        }
    }

    public static void Initialize(Window? mainWindow = null)
    {
        // Kept as a presentation compatibility entry point. The focus service has application
        // lifetime and therefore must not subscribe to each transient CV window.
        _ = mainWindow;
        RecordCurrentForegroundWindow();
    }

    public static void RecordCurrentForegroundWindow()
    {
        GetService()?.RecordCurrentForegroundWindow();
    }

    public static bool RestorePreviousFocus() =>
        GetService()?.TryRestorePreviousForegroundWindow() == true;

    private static IFocusService? GetService()
    {
        lock (Gate)
        {
            return _service;
        }
    }
}
