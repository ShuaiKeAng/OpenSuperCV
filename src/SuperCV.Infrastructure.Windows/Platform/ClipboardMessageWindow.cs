using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SuperCV.Infrastructure.Windows.Platform;

internal sealed class ClipboardMessageWindow : IAsyncDisposable
{
    private const uint StartListenerMessage = NativeMethods.WmApp + 1;
    private const uint StopListenerMessage = NativeMethods.WmApp + 2;

    private static readonly NativeMethods.WindowProcedure SharedWindowProcedure = WindowProcedure;

    private readonly object _gate = new();
    private readonly Action _clipboardUpdated;
    private readonly string _className = $"SuperCV.Clipboard.{Guid.NewGuid():N}";

    private TaskCompletionSource<Exception?>? _ready;
    private Thread? _thread;
    private nint _window;
    private int _monitoring;
    private int _lastListenerError;
    private bool _disposed;

    internal ClipboardMessageWindow(Action clipboardUpdated)
    {
        _clipboardUpdated = clipboardUpdated ?? throw new ArgumentNullException(nameof(clipboardUpdated));
    }

    internal bool IsMonitoring => Volatile.Read(ref _monitoring) != 0;

    internal nint WindowHandle
    {
        get
        {
            lock (_gate)
            {
                return _window;
            }
        }
    }

    internal async ValueTask<nint> GetWindowHandleAsync(CancellationToken cancellationToken)
    {
        await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_window == nint.Zero)
            {
                throw new InvalidOperationException("The clipboard message window is unavailable.");
            }

            return _window;
        }
    }

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsMonitoring)
            {
                return;
            }

            Volatile.Write(ref _lastListenerError, 0);
            nint result = NativeMethods.SendMessage(
                _window,
                StartListenerMessage,
                nuint.Zero,
                nint.Zero);
            if (result == nint.Zero)
            {
                int error = Volatile.Read(ref _lastListenerError);
                throw new Win32Exception(
                    error,
                    "Unable to register the clipboard format listener.");
            }
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<Exception?>? readyTask;
        lock (_gate)
        {
            if (_thread is null || !IsMonitoring)
            {
                return;
            }

            readyTask = _ready?.Task;
        }

        if (readyTask is not null)
        {
            _ = await readyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_window == nint.Zero || !IsMonitoring)
            {
                return;
            }

            Volatile.Write(ref _lastListenerError, 0);
            nint result = NativeMethods.SendMessage(
                _window,
                StopListenerMessage,
                nuint.Zero,
                nint.Zero);
            if (result == nint.Zero)
            {
                int error = Volatile.Read(ref _lastListenerError);
                throw new Win32Exception(
                    error,
                    "Unable to unregister the clipboard format listener.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Thread? thread;
        Task<Exception?>? readyTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            readyTask = _ready?.Task;
        }

        if (thread is null)
        {
            return;
        }

        if (readyTask is not null)
        {
            _ = await readyTask.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_window != nint.Zero)
            {
                if (IsMonitoring)
                {
                    _ = NativeMethods.SendMessage(
                        _window,
                        StopListenerMessage,
                        nuint.Zero,
                        nint.Zero);
                }

                _ = NativeMethods.SendMessage(
                    _window,
                    NativeMethods.WmClose,
                    nuint.Zero,
                    nint.Zero);
            }
        }

        if (thread.IsAlive)
        {
            await Task.Run(() => thread.Join()).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureWindowAsync(CancellationToken cancellationToken)
    {
        Task<Exception?> readyTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _ready = new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _thread = new Thread(MessageLoop)
                {
                    IsBackground = true,
                    Name = "SuperCV clipboard listener",
                };
                _thread.Start();
            }

            readyTask = _ready!.Task;
        }

        Exception? startupError = await readyTask
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (startupError is not null)
        {
            throw new InvalidOperationException(
                "Unable to create the clipboard listener window.",
                startupError);
        }
    }

    private void MessageLoop()
    {
        TaskCompletionSource<Exception?> ready;
        lock (_gate)
        {
            ready = _ready!;
        }

        nint instance = nint.Zero;
        ushort classAtom = 0;
        GCHandle selfHandle = default;
        bool startupCompleted = false;

        try
        {
            instance = NativeMethods.GetModuleHandle(null);
            if (instance == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to get the current module handle.");
            }

            var windowClass = new NativeMethods.WindowClass
            {
                Size = checked((uint)Marshal.SizeOf<NativeMethods.WindowClass>()),
                Instance = instance,
                ClassName = _className,
                WindowProcedure = SharedWindowProcedure,
            };
            classAtom = NativeMethods.RegisterClassEx(ref windowClass);
            if (classAtom == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to register the clipboard listener window class.");
            }

            selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            nint window = NativeMethods.CreateWindowEx(
                0,
                _className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HwndMessage,
                nint.Zero,
                instance,
                GCHandle.ToIntPtr(selfHandle));
            if (window == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to create the clipboard listener window.");
            }

            lock (_gate)
            {
                _window = window;
            }

            startupCompleted = true;
            ready.TrySetResult(null);

            while (true)
            {
                int result = NativeMethods.GetMessage(
                    out NativeMethods.Message message,
                    nint.Zero,
                    0,
                    0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The clipboard listener message loop failed.");
                }

                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            if (!startupCompleted)
            {
                ready.TrySetResult(exception);
            }
            else
            {
                Trace.TraceError("Clipboard listener thread failed: {0}", exception);
            }
        }
        finally
        {
            nint window;
            lock (_gate)
            {
                window = _window;
                _window = nint.Zero;
            }

            if (window != nint.Zero && NativeMethods.IsWindow(window))
            {
                _ = NativeMethods.DestroyWindow(window);
            }

            Volatile.Write(ref _monitoring, 0);
            if (selfHandle.IsAllocated)
            {
                selfHandle.Free();
            }

            if (classAtom != 0 && instance != nint.Zero)
            {
                _ = NativeMethods.UnregisterClass(_className, instance);
            }

            if (!startupCompleted)
            {
                ready.TrySetResult(new InvalidOperationException(
                    "The clipboard listener thread exited before startup completed."));
            }
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            nint userData;
            if (message == NativeMethods.WmNcCreate)
            {
                NativeMethods.CreateStruct create =
                    Marshal.PtrToStructure<NativeMethods.CreateStruct>(lParam);
                userData = create.CreateParameters;
                _ = NativeMethods.SetWindowLongPtr(
                    window,
                    NativeMethods.GwlpUserData,
                    userData);
            }
            else
            {
                userData = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlpUserData);
            }

            if (userData != nint.Zero)
            {
                GCHandle handle = GCHandle.FromIntPtr(userData);
                if (handle.Target is ClipboardMessageWindow owner)
                {
                    return owner.ProcessWindowMessage(window, message, wParam, lParam);
                }
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Clipboard listener window procedure failed: {0}", exception);
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private nint ProcessWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        _ = wParam;
        _ = lParam;

        switch (message)
        {
            case StartListenerMessage:
                if (IsMonitoring)
                {
                    return new nint(1);
                }

                if (!NativeMethods.AddClipboardFormatListener(window))
                {
                    Volatile.Write(ref _lastListenerError, Marshal.GetLastWin32Error());
                    return nint.Zero;
                }

                Volatile.Write(ref _monitoring, 1);
                return new nint(1);

            case StopListenerMessage:
                if (!IsMonitoring)
                {
                    return new nint(1);
                }

                if (!NativeMethods.RemoveClipboardFormatListener(window))
                {
                    Volatile.Write(ref _lastListenerError, Marshal.GetLastWin32Error());
                    return nint.Zero;
                }

                Volatile.Write(ref _monitoring, 0);
                return new nint(1);

            case NativeMethods.WmClipboardUpdate:
                _clipboardUpdated();
                return nint.Zero;

            case NativeMethods.WmClose:
                _ = NativeMethods.DestroyWindow(window);
                return nint.Zero;

            case NativeMethods.WmDestroy:
                if (IsMonitoring)
                {
                    _ = NativeMethods.RemoveClipboardFormatListener(window);
                    Volatile.Write(ref _monitoring, 0);
                }

                NativeMethods.PostQuitMessage(0);
                return nint.Zero;

            case NativeMethods.WmNcDestroy:
                _ = NativeMethods.SetWindowLongPtr(
                    window,
                    NativeMethods.GwlpUserData,
                    nint.Zero);
                break;
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }
}
