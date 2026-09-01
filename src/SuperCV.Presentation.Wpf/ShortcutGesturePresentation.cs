using System.Windows.Input;
using SuperCV.Domain.Settings;

namespace SuperCV;

internal static class ShortcutGesturePresentation
{
    public static string Format(ShortcutGesture gesture)
    {
        if (!gesture.IsValid)
        {
            return LocalizationService.Current.T("未设置");
        }

        var parts = GetModifierParts(gesture.Modifiers);
        parts.Add(gesture.VirtualKey is int virtualKey
            ? FormatVirtualKey(virtualKey)
            : FormatMouseButton(gesture.MouseButton!.Value));
        return string.Join(" + ", parts);
    }

    public static string FormatModifiers(ShortcutModifiers modifiers) =>
        ClipboardShortcutDefaults.AreModifiersValid(modifiers)
            ? string.Join(" + ", GetModifierParts(modifiers))
            : LocalizationService.Current.T("未设置");

    private static List<string> GetModifierParts(ShortcutModifiers modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & ShortcutModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ShortcutModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ShortcutModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ShortcutModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }

        return parts;
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 ||
            virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            return LocalizationService.Current.T($"数字键盘 {virtualKey - 0x60}");
        }

        return KeyInterop.KeyFromVirtualKey(virtualKey) switch
        {
            Key.Back => "Backspace",
            Key.Tab => "Tab",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Space => "Space",
            Key.PageUp => "Page Up",
            Key.PageDown => "Page Down",
            Key.End => "End",
            Key.Home => "Home",
            Key.Left => "←",
            Key.Up => "↑",
            Key.Right => "→",
            Key.Down => "↓",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key key when key != Key.None => key.ToString(),
            _ => $"VK {virtualKey:X2}",
        };
    }

    private static string FormatMouseButton(ShortcutMouseButton mouseButton) =>
        mouseButton switch
        {
            ShortcutMouseButton.Left => LocalizationService.Current.T("鼠标左键"),
            ShortcutMouseButton.Right => LocalizationService.Current.T("鼠标右键"),
            ShortcutMouseButton.Middle => LocalizationService.Current.T("鼠标中键"),
            ShortcutMouseButton.XButton1 => LocalizationService.Current.T("鼠标侧键 1"),
            ShortcutMouseButton.XButton2 => LocalizationService.Current.T("鼠标侧键 2"),
            _ => LocalizationService.Current.T("鼠标键"),
        };
}
