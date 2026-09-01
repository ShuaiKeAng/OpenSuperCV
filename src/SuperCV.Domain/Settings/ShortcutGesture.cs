namespace SuperCV.Domain.Settings;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public enum ShortcutMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    XButton1 = 3,
    XButton2 = 4,
}

public readonly record struct ShortcutGesture(
    ShortcutModifiers Modifiers,
    int? VirtualKey,
    ShortcutMouseButton? MouseButton)
{
    private const ShortcutModifiers AllModifiers =
        ShortcutModifiers.Alt |
        ShortcutModifiers.Control |
        ShortcutModifiers.Shift |
        ShortcutModifiers.Windows;

    public ShortcutGesture(ShortcutModifiers modifiers, int virtualKey)
        : this(modifiers, virtualKey, MouseButton: null)
    {
    }

    public ShortcutGesture(ShortcutModifiers modifiers, ShortcutMouseButton mouseButton)
        : this(modifiers, VirtualKey: null, mouseButton)
    {
    }

    public bool IsValid =>
        Modifiers != ShortcutModifiers.None &&
        (Modifiers & ~AllModifiers) == 0 &&
        (VirtualKey.HasValue ^ MouseButton.HasValue) &&
        (!VirtualKey.HasValue || IsSupportedVirtualKey(VirtualKey.Value)) &&
        (!MouseButton.HasValue || Enum.IsDefined(MouseButton.Value));

    private static bool IsSupportedVirtualKey(int virtualKey) =>
        virtualKey is >= 0x08 and <= 0xFE &&
        virtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C);
}

public static class ClipboardShortcutDefaults
{
    private const ShortcutModifiers AllModifiers =
        ShortcutModifiers.Alt |
        ShortcutModifiers.Control |
        ShortcutModifiers.Shift |
        ShortcutModifiers.Windows;

    public const ShortcutModifiers AbsoluteEntriesModifiers =
        ShortcutModifiers.Alt | ShortcutModifiers.Shift;

    public const ShortcutModifiers VisibleEntriesModifiers =
        ShortcutModifiers.Alt | ShortcutModifiers.Shift;

    public static readonly ShortcutGesture MoveWindow = new(
        ShortcutModifiers.Alt,
        ShortcutMouseButton.Left);

    public static readonly ShortcutGesture PasteOlder = new(
        ShortcutModifiers.Alt | ShortcutModifiers.Shift,
        0x4D);

    public static readonly ShortcutGesture PasteNewer = new(
        ShortcutModifiers.Alt | ShortcutModifiers.Shift,
        0x4E);

    public static readonly ShortcutGesture OpenSuperCV = new(
        ShortcutModifiers.Windows,
        0x56);

    public static bool AreModifiersValid(ShortcutModifiers modifiers) =>
        modifiers != ShortcutModifiers.None &&
        (modifiers & ~AllModifiers) == 0;

    public static bool IsReserved(ShortcutGesture gesture) =>
        IsReserved(
            gesture,
            AbsoluteEntriesModifiers,
            VisibleEntriesModifiers);

    public static bool IsReserved(
        ShortcutGesture gesture,
        ShortcutModifiers absoluteEntriesModifiers,
        ShortcutModifiers visibleEntriesModifiers)
    {
        if (gesture == OpenSuperCV)
        {
            return true;
        }

        if (gesture.MouseButton.HasValue ||
            gesture.VirtualKey is not int virtualKey)
        {
            return false;
        }

        return virtualKey is >= 0x30 and <= 0x39
            ? gesture.Modifiers == absoluteEntriesModifiers
            : virtualKey is 0x51 or 0x57 or 0x45 or 0x52 or 0x54 or 0x59 &&
                gesture.Modifiers == visibleEntriesModifiers;
    }

    public static bool IsValidConfiguration(
        ShortcutGesture moveWindow,
        ShortcutGesture pasteOlder,
        ShortcutGesture pasteNewer) =>
        IsValidConfiguration(
            AbsoluteEntriesModifiers,
            VisibleEntriesModifiers,
            moveWindow,
            pasteOlder,
            pasteNewer);

    public static bool IsValidConfiguration(
        ShortcutModifiers absoluteEntriesModifiers,
        ShortcutModifiers visibleEntriesModifiers,
        ShortcutGesture moveWindow,
        ShortcutGesture pasteOlder,
        ShortcutGesture pasteNewer) =>
        AreModifiersValid(absoluteEntriesModifiers) &&
        AreModifiersValid(visibleEntriesModifiers) &&
        moveWindow.IsValid &&
        pasteOlder.IsValid &&
        pasteNewer.IsValid &&
        !IsReserved(moveWindow, absoluteEntriesModifiers, visibleEntriesModifiers) &&
        !IsReserved(pasteOlder, absoluteEntriesModifiers, visibleEntriesModifiers) &&
        !IsReserved(pasteNewer, absoluteEntriesModifiers, visibleEntriesModifiers) &&
        moveWindow != pasteOlder &&
        moveWindow != pasteNewer &&
        pasteOlder != pasteNewer;
}
