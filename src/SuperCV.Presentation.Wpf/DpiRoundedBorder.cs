using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SuperCV;

/// <summary>
/// A rounded container whose outline and content inset are measured in device pixels.
/// This prevents fractional-DPI rounding from putting an extra pixel on one side.
/// </summary>
public sealed class DpiRoundedBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty =
        Panel.BackgroundProperty.AddOwner(
            typeof(DpiRoundedBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty =
        DependencyProperty.Register(
            nameof(BorderBrush),
            typeof(Brush),
            typeof(DpiRoundedBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(DpiRoundedBorder),
            new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not UIElement child)
        {
            return default;
        }

        double inset = GetDevicePixelInset();
        child.Measure(Deflate(availableSize, inset));
        return new Size(child.DesiredSize.Width + (inset * 2), child.DesiredSize.Height + (inset * 2));
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        if (Child is UIElement child)
        {
            double inset = GetDevicePixelInset();
            child.Arrange(new Rect(
                inset,
                inset,
                Math.Max(0, arrangeBounds.Width - (inset * 2)),
                Math.Max(0, arrangeBounds.Height - (inset * 2))));
        }

        return arrangeBounds;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double inset = GetDevicePixelInset();
        DrawRoundedRectangle(drawingContext, BorderBrush, new Rect(0, 0, ActualWidth, ActualHeight), CornerRadius);

        Rect interior = new(
            inset,
            inset,
            Math.Max(0, ActualWidth - (inset * 2)),
            Math.Max(0, ActualHeight - (inset * 2)));
        DrawRoundedRectangle(drawingContext, Background, interior, Deflate(CornerRadius, inset));
    }

    private double GetDevicePixelInset()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return 1 / Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private static Size Deflate(Size size, double inset) => new(
        Math.Max(0, size.Width - (inset * 2)),
        Math.Max(0, size.Height - (inset * 2)));

    private static CornerRadius Deflate(CornerRadius radius, double inset) => new(
        Math.Max(0, radius.TopLeft - inset),
        Math.Max(0, radius.TopRight - inset),
        Math.Max(0, radius.BottomRight - inset),
        Math.Max(0, radius.BottomLeft - inset));

    private static void DrawRoundedRectangle(
        DrawingContext drawingContext,
        Brush? brush,
        Rect bounds,
        CornerRadius cornerRadius)
    {
        if (brush is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double radius = Math.Min(
            Math.Min(bounds.Width, bounds.Height) / 2,
            Math.Max(
                Math.Max(cornerRadius.TopLeft, cornerRadius.TopRight),
                Math.Max(cornerRadius.BottomLeft, cornerRadius.BottomRight)));
        drawingContext.DrawRoundedRectangle(brush, null, bounds, radius, radius);
    }
}
