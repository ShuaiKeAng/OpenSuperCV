using System.Windows;
using System.Windows.Media.Animation;

namespace SuperCV;

/// <summary>
/// Applies the shared entrance motion to auxiliary top-level windows. The main clipboard
/// surfaces and the tray menu have their own visibility/motion lifecycles and are excluded.
/// </summary>
internal static class PopupWindowAnimator
{
    private static readonly Duration FadeInDuration =
        new(TimeSpan.FromMilliseconds(180));

    internal static bool TryBeginFadeIn(Window window, bool advancedAnimationEnabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!advancedAnimationEnabled || !IsPopupWindow(window))
        {
            return false;
        }

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = FadeInDuration,
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut,
            },
            FillBehavior = FillBehavior.Stop,
        };

        // FillBehavior.Stop restores the window's original opacity (including any binding)
        // after the entrance finishes, so the animation never owns its lasting appearance.
        window.BeginAnimation(
            UIElement.OpacityProperty,
            fadeIn,
            HandoffBehavior.SnapshotAndReplace);
        return true;
    }

    private static bool IsPopupWindow(Window window) =>
        window is not SuperCVWindow
        and not CV
        and not TrayMenuWindow;
}
