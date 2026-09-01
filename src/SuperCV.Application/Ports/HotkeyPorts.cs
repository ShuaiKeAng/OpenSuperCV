namespace SuperCV.Application.Ports;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public enum HotkeyMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    XButton1 = 3,
    XButton2 = 4,
}

public readonly record struct HotkeyGesture(
    HotkeyModifiers Modifiers,
    int? VirtualKey,
    HotkeyMouseButton? MouseButton)
{
    public HotkeyGesture(HotkeyModifiers modifiers, HotkeyMouseButton mouseButton)
        : this(modifiers, VirtualKey: null, mouseButton)
    {
    }

    public HotkeyGesture(HotkeyModifiers modifiers, int virtualKey)
        : this(modifiers, virtualKey, MouseButton: null)
    {
    }

    public bool IsValid => VirtualKey.HasValue ^ MouseButton.HasValue;
}

public sealed record HotkeyRegistrationOptions
{
    public bool SuppressInput { get; init; }
}

public interface IHotkeyRegistration : IAsyncDisposable
{
    HotkeyGesture Gesture { get; }
}

public interface IHotkeyService : IAsyncDisposable
{
    ValueTask<IHotkeyRegistration> RegisterAsync(
        HotkeyGesture gesture,
        Func<CancellationToken, ValueTask> callback,
        HotkeyRegistrationOptions? options = null,
        CancellationToken cancellationToken = default);
}
