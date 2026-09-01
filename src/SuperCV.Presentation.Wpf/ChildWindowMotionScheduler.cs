using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SuperCV;

internal readonly record struct NativeChildWindowPosition(
    CV Window,
    nint Handle,
    int PhysicalX,
    int PhysicalY);

internal readonly record struct NativeChildWindowFrameContext(
    int TargetPhysicalLeft,
    int TargetPhysicalTop,
    double TargetLogicalLeft,
    double TargetLogicalTop,
    double DpiScale);

/// <summary>
/// Collects all entry-window moves for one animation frame and commits them in one native window
/// positioning transaction. This preserves the existing independent-window presentation while
/// removing repeated compositor/layout synchronization from the per-entry hot path.
/// </summary>
internal sealed class ChildWindowMotionScheduler
{
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private const uint PositionFlags = NoSize | NoZOrder | NoActivate | NoOwnerZOrder;

    private readonly List<PendingPosition> _pending = new(capacity: 8);
    private readonly List<NativeChildWindowPosition> _nativePositions = new(capacity: 8);
    private NativeChildWindowFrameContext _frameContext;
    private bool _hasFrameContext;
    private bool _isCollecting;

    internal void BeginFrame(SuperCVWindow targetWindow)
    {
        ArgumentNullException.ThrowIfNull(targetWindow);
        _pending.Clear();
        _nativePositions.Clear();
        _hasFrameContext = TryCreateFrameContext(targetWindow, out _frameContext);
        _isCollecting = true;
    }

    internal void Queue(
        CV window,
        double logicalX,
        double logicalY,
        bool verifyActualPosition)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SetMotionPosition(logicalX, logicalY);

        if (!_isCollecting)
        {
            MoveImmediately(window, logicalX, logicalY);
            return;
        }

        _pending.Add(new PendingPosition(
            window,
            logicalX,
            logicalY,
            verifyActualPosition));
    }

    internal void CommitFrame()
    {
        if (!_isCollecting)
        {
            return;
        }

        _isCollecting = false;
        foreach (PendingPosition pending in _pending)
        {
            if (pending.Window.TryPrepareNativePosition(
                    pending.LogicalX,
                    pending.LogicalY,
                    pending.VerifyActualPosition,
                    _hasFrameContext,
                    _frameContext,
                    out NativeChildWindowPosition position))
            {
                _nativePositions.Add(position);
            }
        }

        if (_nativePositions.Count == 0)
        {
            return;
        }

        nint deferredHandle = BeginDeferWindowPos(_nativePositions.Count);
        bool batchSucceeded = deferredHandle != 0;
        if (batchSucceeded)
        {
            foreach (NativeChildWindowPosition position in _nativePositions)
            {
                deferredHandle = DeferWindowPos(
                    deferredHandle,
                    position.Handle,
                    0,
                    position.PhysicalX,
                    position.PhysicalY,
                    0,
                    0,
                    PositionFlags);
                if (deferredHandle == 0)
                {
                    batchSucceeded = false;
                    break;
                }
            }
        }

        if (batchSucceeded)
        {
            batchSucceeded = EndDeferWindowPos(deferredHandle);
        }

        if (batchSucceeded)
        {
            foreach (NativeChildWindowPosition position in _nativePositions)
            {
                position.Window.AcceptNativePosition(position.PhysicalX, position.PhysicalY);
            }

            return;
        }

        // Resource pressure can make a defer transaction unavailable. Individual native moves
        // retain correctness and visual behavior as a graceful fallback.
        foreach (NativeChildWindowPosition position in _nativePositions)
        {
            ApplyNativePosition(position);
        }
    }

    internal static void MoveImmediately(CV window, double logicalX, double logicalY)
    {
        window.SetMotionPosition(logicalX, logicalY);
        bool hasContext = TryCreateFrameContext(window.TargetWindow, out var context);
        if (window.TryPrepareNativePosition(
                logicalX,
                logicalY,
                verifyActualPosition: true,
                hasContext,
                context,
                out NativeChildWindowPosition position))
        {
            ApplyNativePosition(position);
        }
    }

    private static void ApplyNativePosition(NativeChildWindowPosition position)
    {
        if (SetWindowPos(
                position.Handle,
                0,
                position.PhysicalX,
                position.PhysicalY,
                0,
                0,
                PositionFlags))
        {
            position.Window.AcceptNativePosition(position.PhysicalX, position.PhysicalY);
        }
    }

    private static bool TryCreateFrameContext(
        SuperCVWindow targetWindow,
        out NativeChildWindowFrameContext context)
    {
        nint targetHandle = new WindowInteropHelper(targetWindow).Handle;
        if (targetHandle == 0 ||
            !GetWindowRect(targetHandle, out NativeRect targetRect))
        {
            context = default;
            return false;
        }

        uint dpi = GetDpiForWindow(targetHandle);
        double scale = (dpi == 0 ? 96u : dpi) / 96.0;
        context = new NativeChildWindowFrameContext(
            targetRect.Left,
            targetRect.Top,
            targetWindow.Left,
            targetWindow.Top,
            scale);
        return true;
    }

    private readonly record struct PendingPosition(
        CV Window,
        double LogicalX,
        double LogicalY,
        bool VerifyActualPosition);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("User32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("User32.dll")]
    private static extern nint BeginDeferWindowPos(int numberOfWindows);

    [DllImport("User32.dll")]
    private static extern nint DeferWindowPos(
        nint deferWindowPositionInfo,
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(nint deferWindowPositionInfo);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
