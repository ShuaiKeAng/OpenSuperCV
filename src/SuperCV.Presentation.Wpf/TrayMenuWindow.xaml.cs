using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SuperCV;

public partial class TrayMenuWindow : Window
{
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Func<bool> _getMonitoringState;
    private readonly Action _openApplication;
    private readonly Action _toggleMonitoring;
    private readonly Action _exitApplication;
    private bool _closeRequested;

    internal TrayMenuWindow(
        Func<bool> getMonitoringState,
        Action openApplication,
        Action toggleMonitoring,
        Action exitApplication)
    {
        _getMonitoringState = getMonitoringState
            ?? throw new ArgumentNullException(nameof(getMonitoringState));
        _openApplication = openApplication
            ?? throw new ArgumentNullException(nameof(openApplication));
        _toggleMonitoring = toggleMonitoring
            ?? throw new ArgumentNullException(nameof(toggleMonitoring));
        _exitApplication = exitApplication
            ?? throw new ArgumentNullException(nameof(exitApplication));

        InitializeComponent();
        RefreshMonitoringState();
    }

    internal void ShowAt(System.Drawing.Point cursorPosition)
    {
        RefreshMonitoringState();
        // Show creates the native handle needed for DPI-aware placement. Keep the
        // window transparent until that placement is complete so its default
        // manual location (0,0) can never be presented for one frame.
        Opacity = 0;
        Show();
        UpdateLayout();
        PositionNear(cursorPosition);
        Opacity = 1;
        Activate();
        Focus();
    }

    private void PositionNear(System.Drawing.Point cursorPosition)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        System.Drawing.Rectangle workArea =
            System.Windows.Forms.Screen.FromPoint(cursorPosition).WorkingArea;

        // The first move can cross into a monitor with a different DPI. WPF processes the
        // resulting DPI change synchronously, so a second pass can anchor the resized menu
        // precisely to the tray icon on mixed-DPI desktops.
        for (int pass = 0; pass < 2; pass++)
        {
            uint dpi = GetDpiForWindow(handle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
            int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));

            const int edgeGap = 8;
            int left = Math.Clamp(
                cursorPosition.X - width,
                workArea.Left + edgeGap,
                Math.Max(workArea.Left + edgeGap, workArea.Right - width - edgeGap));
            int top = cursorPosition.Y - height - edgeGap;
            if (top < workArea.Top + edgeGap)
            {
                top = Math.Min(
                    cursorPosition.Y + edgeGap,
                    Math.Max(workArea.Top + edgeGap, workArea.Bottom - height - edgeGap));
            }

            _ = SetWindowPos(
                handle,
                HwndTopmost,
                left,
                top,
                width,
                height,
                SwpNoOwnerZOrder | SwpShowWindow);
            UpdateLayout();
        }
    }

    private void RefreshMonitoringState()
    {
        bool isMonitoring = _getMonitoringState();
        MonitoringTitleText.Text = LocalizationService.Current.T(
            isMonitoring ? "关闭列表更新" : "打开列表更新");
        MonitoringDescriptionText.Text = isMonitoring
            ? LocalizationService.Current.T("正在监听内容更新")
            : LocalizationService.Current.T("剪贴板监听已暂停");
        MonitoringStatusText.Text = LocalizationService.Current.T(isMonitoring ? "监听中" : "已暂停");
        MonitoringStatusDot.Fill = isMonitoring
            ? (Brush)FindResource("TrayStatusOnBrush")
            : (Brush)FindResource("Brush.Text.Muted");
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _openApplication();
    }

    private void MonitoringButton_Click(object sender, RoutedEventArgs e)
    {
        _toggleMonitoring();
        RefreshMonitoringState();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        _exitApplication();
    }

    internal void Dismiss()
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closeRequested = true;
        base.OnClosing(e);
    }

    private void Window_Deactivated(object? sender, EventArgs e) => Dismiss();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Dismiss();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
