using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SuperCV.Application.Ports;

namespace SuperCV;

/// <summary>
/// Keeps the legacy presentation API while delegating global input handling to
/// the application-level hotkey service.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private readonly object _gate = new();
    private readonly IHotkeyService _hotkeyService;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<InputCombination, RegisteredHotkey> _registrations = [];
    private bool _intercept = true;
    private bool _disposed;

    public GlobalInputHook()
        : this(ResolveHotkeyService(), ResolveDispatcher())
    {
    }

    internal GlobalInputHook(IHotkeyService hotkeyService, Dispatcher dispatcher)
    {
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Whether a matched gesture is prevented from reaching other applications.
    /// The compatibility adapter defaults to true because the original Alt+mouse move gesture
    /// consumes its triggering click; the reusable infrastructure port itself defaults to false.
    /// </summary>
    public bool Intercept
    {
        get
        {
            lock (_gate)
            {
                return _intercept;
            }
        }
        set
        {
            List<RegisteredHotkey>? retired = null;
            List<RegisteredHotkey>? replacements = null;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_intercept == value)
                {
                    return;
                }

                try
                {
                    replacements = new List<RegisteredHotkey>(_registrations.Count);
                    foreach (RegisteredHotkey current in _registrations.Values)
                    {
                        replacements.Add(CreateRegistration(
                            current.Combination,
                            current.Callback,
                            value,
                            activate: false));
                    }

                    retired = [.. _registrations.Values];
                    foreach (RegisteredHotkey current in retired)
                    {
                        current.Disable();
                    }

                    foreach (RegisteredHotkey replacement in replacements)
                    {
                        replacement.Enable();
                    }

                    _registrations.Clear();
                    foreach (RegisteredHotkey replacement in replacements)
                    {
                        _registrations.Add(replacement.Combination, replacement);
                    }

                    _intercept = value;
                }
                catch
                {
                    if (retired is not null)
                    {
                        foreach (RegisteredHotkey current in retired)
                        {
                            current.Enable();
                        }
                    }

                    if (replacements is not null)
                    {
                        DisposeRegistrations(replacements);
                    }

                    throw;
                }
            }

            if (retired is not null)
            {
                DisposeRegistrations(retired);
            }
        }
    }

    public void RegisterHotkey(
        ModifierKeys modifiers,
        Key? key,
        MouseButton? mouseButton,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var combination = new InputCombination(modifiers, key, mouseButton);
        ValidateCombination(combination);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registrations.ContainsKey(combination))
            {
                throw new InvalidOperationException("该快捷键已注册");
            }

            RegisteredHotkey registration = CreateRegistration(
                combination,
                callback,
                _intercept,
                activate: true);
            _registrations.Add(combination, registration);
        }
    }

    public void RegisterHotkey(ModifierKeys modifiers, Key key, Action callback)
    {
        RegisterHotkey(modifiers, key, null, callback);
    }

    public void RegisterHotkey(ModifierKeys modifiers, MouseButton mouseButton, Action callback)
    {
        RegisterHotkey(modifiers, null, mouseButton, callback);
    }

    public void UnregisterHotkey(
        ModifierKeys modifiers,
        Key? key,
        MouseButton? mouseButton)
    {
        RegisteredHotkey? registration;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var combination = new InputCombination(modifiers, key, mouseButton);
            if (!_registrations.Remove(combination, out registration))
            {
                return;
            }
        }

        DisposeRegistration(registration);
    }

    /// <summary>
    /// Idempotently stops all registrations owned by this adapter.
    /// </summary>
    public void Stop()
    {
        List<RegisteredHotkey> registrations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registrations = [.. _registrations.Values];
            _registrations.Clear();
        }

        DisposeRegistrations(registrations);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private RegisteredHotkey CreateRegistration(
        InputCombination combination,
        Action callback,
        bool suppressInput,
        bool activate)
    {
        HotkeyGesture gesture = ToGesture(combination);
        var state = new RegisteredHotkey(combination, callback);
        state.PlatformRegistration = _hotkeyService.RegisterAsync(
                gesture,
                cancellationToken => ExecuteRegistrationAsync(state, cancellationToken),
                new HotkeyRegistrationOptions { SuppressInput = suppressInput })
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (activate)
        {
            state.Enable();
        }

        return state;
    }

    private ValueTask ExecuteRegistrationAsync(
        RegisteredHotkey registration,
        CancellationToken cancellationToken)
    {
        if (!registration.IsEnabled)
        {
            return ValueTask.CompletedTask;
        }

        return ExecuteOnUiThreadAsync(
            () =>
            {
                if (registration.IsEnabled)
                {
                    registration.Callback();
                }
            },
            cancellationToken);
    }

    private async ValueTask ExecuteOnUiThreadAsync(
        Action callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            callback();
            return;
        }

        DispatcherOperation operation;
        try
        {
            operation = _dispatcher.InvokeAsync(
                callback,
                DispatcherPriority.Input,
                cancellationToken);
        }
        catch (InvalidOperationException) when (
            _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        await operation.Task.ConfigureAwait(false);
    }

    private static HotkeyGesture ToGesture(InputCombination combination)
    {
        HotkeyModifiers modifiers = ToHotkeyModifiers(combination.Modifiers);
        if (combination.Key is Key key)
        {
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey is <= 0 or > 0xFF)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(combination),
                    "The keyboard key does not map to a valid Windows virtual-key code.");
            }

            return new HotkeyGesture(modifiers, virtualKey);
        }

        return new HotkeyGesture(
            modifiers,
            combination.MouseButton switch
            {
                MouseButton.Left => HotkeyMouseButton.Left,
                MouseButton.Right => HotkeyMouseButton.Right,
                MouseButton.Middle => HotkeyMouseButton.Middle,
                MouseButton.XButton1 => HotkeyMouseButton.XButton1,
                MouseButton.XButton2 => HotkeyMouseButton.XButton2,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(combination),
                    "The mouse button is not supported."),
            });
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            result |= HotkeyModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= HotkeyModifiers.Control;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= HotkeyModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    private static void ValidateCombination(InputCombination combination)
    {
        if (combination.Key.HasValue == combination.MouseButton.HasValue)
        {
            throw new ArgumentException(
                "A hotkey must contain exactly one keyboard or mouse trigger.",
                nameof(combination));
        }
    }

    private static IHotkeyService ResolveHotkeyService()
    {
        return System.Windows.Application.Current is SuperCV.App app && app.Runtime is not null
            ? app.Runtime.Hotkeys
            : throw new InvalidOperationException(
                "The application hotkey service is not initialized.");
    }

    private static Dispatcher ResolveDispatcher()
    {
        return System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "The WPF application dispatcher is not initialized.");
    }

    private static void DisposeRegistrations(IEnumerable<RegisteredHotkey> registrations)
    {
        foreach (RegisteredHotkey registration in registrations)
        {
            DisposeRegistration(registration);
        }
    }

    private static void DisposeRegistration(RegisteredHotkey registration)
    {
        registration.Disable();
        try
        {
            registration.PlatformRegistration?
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Failed to release a global hotkey registration: {0}", exception);
        }
    }

    private readonly record struct InputCombination(
        ModifierKeys Modifiers,
        Key? Key,
        MouseButton? MouseButton);

    private sealed class RegisteredHotkey
    {
        private int _enabled;

        internal RegisteredHotkey(InputCombination combination, Action callback)
        {
            Combination = combination;
            Callback = callback;
        }

        internal InputCombination Combination { get; }

        internal Action Callback { get; }

        internal IHotkeyRegistration? PlatformRegistration { get; set; }

        internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;

        internal void Enable()
        {
            Volatile.Write(ref _enabled, 1);
        }

        internal void Disable()
        {
            Volatile.Write(ref _enabled, 0);
        }
    }
}

public static class WindowExtensions
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 将窗口移动到鼠标当前位置（自动处理 DPI）。
    /// </summary>
    public static void MoveToMousePosition(
        this Window window,
        double offsetX = -35,
        double offsetY = -35)
    {
        if (!GetCursorPos(out POINT mousePosition))
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        uint currentDpi = GetDpiForWindow(handle);
        currentDpi = currentDpi == 0 ? 96u : currentDpi;
        if (!MoveWindow(handle, mousePosition, offsetX, offsetY, currentDpi))
        {
            return;
        }

        // The preliminary native move lets Windows select the destination monitor. Re-read that
        // monitor's DPI and apply the DIP offsets again so mixed-DPI virtual-desktop origins are
        // never converted using the source monitor's scale.
        uint destinationDpi = GetDpiForWindow(handle);
        destinationDpi = destinationDpi == 0 ? 96u : destinationDpi;
        if (destinationDpi != currentDpi)
        {
            _ = MoveWindow(handle, mousePosition, offsetX, offsetY, destinationDpi);
        }
    }

    private static bool MoveWindow(
        IntPtr handle,
        POINT mousePosition,
        double offsetX,
        double offsetY,
        uint dpi) =>
        SetWindowPos(
            handle,
            IntPtr.Zero,
            mousePosition.X + (int)Math.Round(offsetX * dpi / 96.0),
            mousePosition.Y + (int)Math.Round(offsetY * dpi / 96.0),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
}
