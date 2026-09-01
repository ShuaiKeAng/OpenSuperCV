using SuperCV.Application.Ports;

namespace SuperCV.Infrastructure.Windows.Platform;

public sealed class WindowsFocusService : IFocusService
{
    private readonly object _gate = new();
    private ForegroundTarget _previous;

    public void RecordCurrentForegroundWindow()
    {
        nint window = NativeMethods.GetForegroundWindow();
        if (window == nint.Zero ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.IsWindowVisible(window))
        {
            return;
        }

        _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == checked((uint)Environment.ProcessId))
        {
            return;
        }

        lock (_gate)
        {
            _previous = new ForegroundTarget(window, processId);
        }
    }

    public bool TryRestorePreviousForegroundWindow()
    {
        ForegroundTarget target;
        lock (_gate)
        {
            target = _previous;
        }

        if (target.Window == nint.Zero ||
            !NativeMethods.IsWindow(target.Window) ||
            !NativeMethods.IsWindowVisible(target.Window) ||
            NativeMethods.IsIconic(target.Window))
        {
            ClearIfUnchanged(target);
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(target.Window, out uint currentProcessId);
        if (currentProcessId == 0 ||
            currentProcessId != target.ProcessId ||
            currentProcessId == checked((uint)Environment.ProcessId))
        {
            ClearIfUnchanged(target);
            return false;
        }

        if (NativeMethods.GetForegroundWindow() == target.Window)
        {
            return true;
        }

        return NativeMethods.SetForegroundWindow(target.Window);
    }

    private void ClearIfUnchanged(ForegroundTarget target)
    {
        lock (_gate)
        {
            if (_previous == target)
            {
                _previous = default;
            }
        }
    }

    private readonly record struct ForegroundTarget(nint Window, uint ProcessId);
}
