using System.Drawing;
using System.Windows.Threading;

namespace SuperCV;

internal sealed class TrayIconController : IDisposable
{
    private readonly SuperCVWindow _owner;
    private readonly Dispatcher _dispatcher;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private TrayMenuWindow? _menuWindow;
    private bool _disposed;

    internal TrayIconController(SuperCVWindow owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _dispatcher = owner.Dispatcher;
        _owner.Icon =
            ApplicationIconRenderer.TryCreateTaskbarImageSource()
            ?? _owner.Icon;
        _icon =
            ApplicationIconRenderer.TryCreateTrayIcon()
            ?? LoadApplicationIcon();
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Visible = true,
        };
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        UpdateMonitoringStatus();
    }

    internal void UpdateMonitoringStatus()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Text = LocalizationService.Current.T(_owner.MonitorCV
            ? "SuperCV · 列表更新已开启"
            : "SuperCV · 列表更新已暂停");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        _menuWindow?.Dismiss();
        _menuWindow = null;
    }

    private void NotifyIcon_MouseUp(
        object? sender,
        System.Windows.Forms.MouseEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Button == System.Windows.Forms.MouseButtons.Right)
        {
            _ = _dispatcher.BeginInvoke(ShowMenu);
        }
        else if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            _ = _dispatcher.BeginInvoke(_owner.RestoreFromTray);
        }
    }

    private void ShowMenu()
    {
        if (_disposed)
        {
            return;
        }

        _menuWindow?.Dismiss();
        var menuWindow = new TrayMenuWindow(
            () => _owner.MonitorCV,
            _owner.RestoreFromTray,
            _owner.ToggleMonitoringFromTray,
            _owner.RequestExit);
        _menuWindow = menuWindow;
        menuWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menuWindow, menuWindow))
            {
                _menuWindow = null;
            }
        };
        menuWindow.ShowAt(System.Windows.Forms.Cursor.Position);
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                using Icon? extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    return (Icon)extracted.Clone();
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
