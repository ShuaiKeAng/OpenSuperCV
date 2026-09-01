using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SuperCV.Application.Ports;

namespace SuperCV.Infrastructure.Windows.Platform;

public sealed class WindowsHotkeyService : IHotkeyService
{
    private readonly object _gate = new();
    private readonly Dictionary<long, RegistrationState> _registrations = [];
    private readonly ConcurrentQueue<RegistrationState> _callbackQueue = new();
    private readonly HashSet<int> _pressedVirtualKeys = [];
    private readonly HashSet<int> _suppressedVirtualKeys = [];
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly NativeMethods.LowLevelMouseProcedure _mouseProcedure;

    private Dictionary<HotkeyGesture, RegistrationState[]> _registrationLookup = [];
    private TaskCompletionSource<Exception?>? _hookReady;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private long _nextRegistrationId;
    private int _callbackDrainScheduled;
    private bool _disposed;

    public WindowsHotkeyService()
    {
        _keyboardProcedure = KeyboardHookCallback;
        _mouseProcedure = MouseHookCallback;
    }

    public async ValueTask<IHotkeyRegistration> RegisterAsync(
        HotkeyGesture gesture,
        Func<CancellationToken, ValueTask> handler,
        HotkeyRegistrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateGesture(gesture);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureHookThreadAsync(cancellationToken).ConfigureAwait(false);

        RegistrationState state;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            long id = checked(++_nextRegistrationId);
            state = new RegistrationState(
                id,
                gesture,
                handler,
                options?.SuppressInput ?? false);
            _registrations.Add(id, state);
            PublishRegistrationSnapshot();
        }

        return new HotkeyRegistration(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        Thread? hookThread;
        Task<Exception?>? readyTask;
        RegistrationState[] registrations;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registrations = [.. _registrations.Values];
            _registrations.Clear();
            PublishRegistrationSnapshot();
            hookThread = _hookThread;
            readyTask = _hookReady?.Task;
        }

        foreach (RegistrationState registration in registrations)
        {
            registration.Dispose();
        }

        if (hookThread is null)
        {
            return;
        }

        if (readyTask is not null)
        {
            _ = await readyTask.ConfigureAwait(false);
        }

        uint threadId;
        lock (_gate)
        {
            threadId = _hookThreadId;
        }

        if (threadId != 0 && hookThread.IsAlive)
        {
            _ = NativeMethods.PostThreadMessage(
                threadId,
                NativeMethods.WmQuit,
                nuint.Zero,
                nint.Zero);
            await Task.Run(() => hookThread.Join()).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureHookThreadAsync(CancellationToken cancellationToken)
    {
        Task<Exception?> readyTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_hookThread is null)
            {
                _hookReady = new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _hookThread = new Thread(HookThreadMain)
                {
                    IsBackground = true,
                    Name = "SuperCV global input hook",
                };
                _hookThread.Start();
            }

            readyTask = _hookReady!.Task;
        }

        Exception? startupError = await readyTask
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (startupError is not null)
        {
            throw new InvalidOperationException(
                "Unable to install the global input hooks.",
                startupError);
        }
    }

    private void HookThreadMain()
    {
        TaskCompletionSource<Exception?> ready;
        lock (_gate)
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            ready = _hookReady!;
        }

        _ = NativeMethods.PeekMessage(
            out _,
            nint.Zero,
            0,
            0,
            NativeMethods.PmNoRemove);

        bool startupCompleted = false;
        try
        {
            InstallHooks();
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
                        "The global input hook message loop failed.");
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
                Trace.TraceError("Global input hook thread failed: {0}", exception);
            }
        }
        finally
        {
            UninstallHooks();
            if (!startupCompleted)
            {
                ready.TrySetResult(new InvalidOperationException(
                    "The global input hook thread exited before startup completed."));
            }

            lock (_gate)
            {
                _hookThreadId = 0;
            }
        }
    }

    private void InstallHooks()
    {
        nint module = NativeMethods.GetModuleHandle(null);
        if (module == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to get the current module handle.");
        }

        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLowLevel,
            _keyboardProcedure,
            module,
            0);
        if (_keyboardHook == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to install the low-level keyboard hook.");
        }

        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLowLevel,
            _mouseProcedure,
            module,
            0);
        if (_mouseHook == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
            throw new Win32Exception(error, "Unable to install the low-level mouse hook.");
        }
    }

    private void UninstallHooks()
    {
        nint mouseHook = Interlocked.Exchange(ref _mouseHook, nint.Zero);
        if (mouseHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(mouseHook);
        }

        nint keyboardHook = Interlocked.Exchange(ref _keyboardHook, nint.Zero);
        if (keyboardHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(keyboardHook);
        }
    }

    private nint KeyboardHookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        uint message = unchecked((uint)wParam);
        NativeMethods.LowLevelKeyboardData data =
            Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(lParam);
        if ((data.Flags & (NativeMethods.LlkhfInjected |
                NativeMethods.LlkhfLowerIntegrityInjected)) != 0)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        int virtualKey = checked((int)data.VirtualKey);
        if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        {
            _pressedVirtualKeys.Remove(virtualKey);
            bool suppressKeyUp = _suppressedVirtualKeys.Remove(virtualKey);
            return suppressKeyUp
                ? new nint(1)
                : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        if (message is not (NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown))
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        HotkeyModifiers modifiers = ReadModifiers();
        if (!_pressedVirtualKeys.Add(virtualKey))
        {
            bool suppressRepeat = _suppressedVirtualKeys.Contains(virtualKey) ||
                ShouldSuppressMatch(modifiers, virtualKey, mouseButton: null);
            return suppressRepeat
                ? new nint(1)
                : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        bool suppress = DispatchMatches(modifiers, virtualKey, mouseButton: null);
        if (suppress)
        {
            _suppressedVirtualKeys.Add(virtualKey);
            TryMaskWindowsKeyRelease(modifiers);
        }

        return suppress
            ? new nint(1)
            : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        uint message = unchecked((uint)wParam);
        if (!TryGetMouseButton(message, lParam, out HotkeyMouseButton mouseButton))
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        bool suppress = DispatchMatches(ReadModifiers(), virtualKey: null, mouseButton);
        return suppress
            ? new nint(1)
            : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private bool DispatchMatches(
        HotkeyModifiers modifiers,
        int? virtualKey,
        HotkeyMouseButton? mouseButton)
    {
        bool suppress = false;
        var gesture = new HotkeyGesture(modifiers, virtualKey, mouseButton);
        Dictionary<HotkeyGesture, RegistrationState[]> lookup =
            Volatile.Read(ref _registrationLookup);
        if (!lookup.TryGetValue(gesture, out RegistrationState[]? registrations))
        {
            return false;
        }

        foreach (RegistrationState registration in registrations)
        {
            if (registration.IsDisposed)
            {
                continue;
            }

            _callbackQueue.Enqueue(registration);
            suppress |= registration.SuppressInput;
        }

        if (!_callbackQueue.IsEmpty)
        {
            ScheduleCallbackDrain();
        }

        return suppress;
    }

    private bool ShouldSuppressMatch(
        HotkeyModifiers modifiers,
        int? virtualKey,
        HotkeyMouseButton? mouseButton)
    {
        var gesture = new HotkeyGesture(modifiers, virtualKey, mouseButton);
        Dictionary<HotkeyGesture, RegistrationState[]> lookup =
            Volatile.Read(ref _registrationLookup);
        return lookup.TryGetValue(gesture, out RegistrationState[]? registrations) &&
            registrations.Any(static registration =>
                !registration.IsDisposed && registration.SuppressInput);
    }

    private void ScheduleCallbackDrain()
    {
        if (Interlocked.CompareExchange(ref _callbackDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static service => _ = service.DrainCallbacksAsync(),
            this,
            preferLocal: false);
    }

    private async Task DrainCallbacksAsync()
    {
        try
        {
            while (_callbackQueue.TryDequeue(out RegistrationState? registration))
            {
                if (registration.IsDisposed)
                {
                    continue;
                }

                try
                {
                    await registration.Handler(registration.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (registration.Token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Global hotkey handler failed: {0}", exception);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _callbackDrainScheduled, 0);
            if (!_callbackQueue.IsEmpty)
            {
                ScheduleCallbackDrain();
            }
        }
    }

    private void RemoveRegistration(long id)
    {
        RegistrationState? registration;
        lock (_gate)
        {
            if (!_registrations.Remove(id, out registration))
            {
                return;
            }

            PublishRegistrationSnapshot();
        }

        registration.Dispose();
    }

    private void PublishRegistrationSnapshot()
    {
        Dictionary<HotkeyGesture, RegistrationState[]> lookup = _registrations.Values
            .GroupBy(static registration => registration.Gesture)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray());
        Volatile.Write(ref _registrationLookup, lookup);
    }

    private static HotkeyModifiers ReadModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (IsPressed(NativeMethods.VkMenu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsPressed(NativeMethods.VkControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsPressed(NativeMethods.VkShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsPressed(NativeMethods.VkLWin) || IsPressed(NativeMethods.VkRWin))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    internal static bool ShouldMaskWindowsKeyRelease(
        HotkeyModifiers modifiers,
        bool suppressInput) =>
        suppressInput &&
        (modifiers & HotkeyModifiers.Windows) != 0;

    internal static NativeMethods.Input[] CreateWindowsKeyReleaseMaskInputs() =>
    [
        CreateKeyboardInput(
            checked((ushort)NativeMethods.VkControl),
            keyUp: false),
        CreateKeyboardInput(
            checked((ushort)NativeMethods.VkControl),
            keyUp: true),
    ];

    private static void TryMaskWindowsKeyRelease(HotkeyModifiers modifiers)
    {
        if (!ShouldMaskWindowsKeyRelease(modifiers, suppressInput: true))
        {
            return;
        }

        // The Shell has already seen Win go down. If the trigger key is swallowed,
        // an otherwise bare Win release opens Start. A harmless injected Ctrl
        // press marks Win as part of a chord; injected events are ignored by this
        // hook and continue to Windows only.
        NativeMethods.Input[] inputs = CreateWindowsKeyReleaseMaskInputs();
        uint inserted = NativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (inserted != inputs.Length)
        {
            Trace.TraceWarning(
                "Unable to mask the Windows-key release after a suppressed hotkey. " +
                "Inserted {0} of {1} input events; Win32 error {2}.",
                inserted,
                inputs.Length,
                Marshal.GetLastWin32Error());
        }
    }

    private static NativeMethods.Input CreateKeyboardInput(
        ushort virtualKey,
        bool keyUp) =>
        new()
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyEventKeyUp : 0,
                },
            },
        };

    private static bool TryGetMouseButton(
        uint message,
        nint lParam,
        out HotkeyMouseButton mouseButton)
    {
        switch (message)
        {
            case NativeMethods.WmLButtonDown:
                mouseButton = HotkeyMouseButton.Left;
                return true;
            case NativeMethods.WmRButtonDown:
                mouseButton = HotkeyMouseButton.Right;
                return true;
            case NativeMethods.WmMButtonDown:
                mouseButton = HotkeyMouseButton.Middle;
                return true;
            case NativeMethods.WmXButtonDown:
                NativeMethods.LowLevelMouseData data =
                    Marshal.PtrToStructure<NativeMethods.LowLevelMouseData>(lParam);
                mouseButton = NativeMethods.HighWord(data.MouseData) switch
                {
                    NativeMethods.XButton1 => HotkeyMouseButton.XButton1,
                    NativeMethods.XButton2 => HotkeyMouseButton.XButton2,
                    _ => default,
                };
                return NativeMethods.HighWord(data.MouseData) is
                    NativeMethods.XButton1 or NativeMethods.XButton2;
            default:
                mouseButton = default;
                return false;
        }
    }

    private static void ValidateGesture(HotkeyGesture gesture)
    {
        if (gesture.VirtualKey.HasValue == gesture.MouseButton.HasValue)
        {
            throw new ArgumentException(
                "A hotkey gesture must contain exactly one trigger input.",
                nameof(gesture));
        }

        if (gesture.VirtualKey is <= 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gesture),
                "The virtual-key code must be between 1 and 255.");
        }
    }

    private sealed class RegistrationState : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationToken _token;
        private int _disposed;

        internal RegistrationState(
            long id,
            HotkeyGesture gesture,
            Func<CancellationToken, ValueTask> handler,
            bool suppressInput)
        {
            Id = id;
            Gesture = gesture;
            Handler = handler;
            SuppressInput = suppressInput;
            _token = _lifetime.Token;
        }

        internal long Id { get; }
        internal HotkeyGesture Gesture { get; }
        internal Func<CancellationToken, ValueTask> Handler { get; }
        internal bool SuppressInput { get; }
        internal CancellationToken Token => _token;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private sealed class HotkeyRegistration : IHotkeyRegistration
    {
        private WindowsHotkeyService? _owner;
        private readonly long _id;

        internal HotkeyRegistration(WindowsHotkeyService owner, RegistrationState state)
        {
            _owner = owner;
            _id = state.Id;
            Gesture = state.Gesture;
        }

        public HotkeyGesture Gesture { get; }

        public ValueTask DisposeAsync()
        {
            WindowsHotkeyService? owner = Interlocked.Exchange(ref _owner, null);
            owner?.RemoveRegistration(_id);
            return ValueTask.CompletedTask;
        }
    }
}
