using System.Windows;
using System.Windows.Media.Animation;

namespace SuperCV;

/// <summary>
/// Preserves smooth transform animation without letting text-bearing visuals stop between
/// physical pixels.
/// </summary>
internal sealed class PixelAlignedDoubleAnimation : DoubleAnimationBase
{
    private static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From),
        typeof(double),
        typeof(PixelAlignedDoubleAnimation));

    private static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To),
        typeof(double),
        typeof(PixelAlignedDoubleAnimation));

    private static readonly DependencyProperty DpiScaleProperty = DependencyProperty.Register(
        nameof(DpiScale),
        typeof(double),
        typeof(PixelAlignedDoubleAnimation),
        new PropertyMetadata(1.0));

    internal PixelAlignedDoubleAnimation(
        double from,
        double to,
        double dpiScale,
        Duration duration)
    {
        From = from;
        To = to;
        DpiScale = dpiScale;
        Duration = duration;
    }

    private PixelAlignedDoubleAnimation()
    {
    }

    private double From
    {
        get => (double)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    private double To
    {
        get => (double)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    private double DpiScale
    {
        get => (double)GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    protected override double GetCurrentValueCore(
        double defaultOriginValue,
        double defaultDestinationValue,
        AnimationClock animationClock)
    {
        double progress = animationClock.CurrentProgress ?? 0;
        double easeOutProgress = 1.0 - Math.Pow(1.0 - progress, 3.0);
        double value = From + ((To - From) * easeOutProgress);
        return PixelAlignedWindowMotion.AlignLogicalCoordinate(value, DpiScale);
    }

    protected override Freezable CreateInstanceCore() => new PixelAlignedDoubleAnimation();
}
