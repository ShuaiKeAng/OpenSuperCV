using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;

namespace SuperCV;

/// <summary>
/// Draws a border's shadow on an isolated visual layer so the effect never
/// rasterizes the text and controls hosted by the foreground content layer.
/// </summary>
public sealed class CrispShadowBorder : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(CrispShadowBorder),
            new FrameworkPropertyMetadata(default(CornerRadius)));

    public static readonly DependencyProperty ShadowEffectProperty =
        DependencyProperty.Register(
            nameof(ShadowEffect),
            typeof(Effect),
            typeof(CrispShadowBorder),
            new FrameworkPropertyMetadata(null));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Effect? ShadowEffect
    {
        get => (Effect?)GetValue(ShadowEffectProperty);
        set => SetValue(ShadowEffectProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Size templateSize = base.MeasureOverride(constraint);
        if (Content is not UIElement content)
        {
            return templateSize;
        }

        Thickness padding = Padding;
        Thickness border = BorderThickness;
        return new Size(
            content.DesiredSize.Width
                + padding.Left
                + padding.Right
                + border.Left
                + border.Right,
            content.DesiredSize.Height
                + padding.Top
                + padding.Bottom
                + border.Top
                + border.Bottom);
    }
}
