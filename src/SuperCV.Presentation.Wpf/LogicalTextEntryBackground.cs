using System.Windows;
using System.Windows.Media;

namespace SuperCV;

/// <summary>
/// Draws a text-card background in the exact 268 × 98 logical coordinate space used by the
/// configuration preview.  A narrow card simply clips the left side of that same canvas.
/// </summary>
public sealed class LogicalTextEntryBackground : FrameworkElement
{
    private const double LogicalWidth = 268;
    private const double LogicalHeight = 98;
    private const double CornerRadius = 14;

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource),
        typeof(ImageSource),
        typeof(LogicalTextEntryBackground),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity),
        typeof(double),
        typeof(LogicalTextEntryBackground),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageScaleProperty = DependencyProperty.Register(
        nameof(ImageScale),
        typeof(double),
        typeof(LogicalTextEntryBackground),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageOffsetXProperty = DependencyProperty.Register(
        nameof(ImageOffsetX),
        typeof(double),
        typeof(LogicalTextEntryBackground),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageOffsetYProperty = DependencyProperty.Register(
        nameof(ImageOffsetY),
        typeof(double),
        typeof(LogicalTextEntryBackground),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public double ImageOpacity
    {
        get => (double)GetValue(ImageOpacityProperty);
        set => SetValue(ImageOpacityProperty, value);
    }

    public double ImageScale
    {
        get => (double)GetValue(ImageScaleProperty);
        set => SetValue(ImageScaleProperty, value);
    }

    public double ImageOffsetX
    {
        get => (double)GetValue(ImageOffsetXProperty);
        set => SetValue(ImageOffsetXProperty, value);
    }

    public double ImageOffsetY
    {
        get => (double)GetValue(ImageOffsetYProperty);
        set => SetValue(ImageOffsetYProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        ImageSource? source = ImageSource;
        if (source is null || source.Width <= 0 || source.Height <= 0 ||
            ActualWidth <= 0 || ActualHeight <= 0 || ImageOpacity <= 0)
        {
            return;
        }

        double logicalLeft = ActualWidth - LogicalWidth;
        double logicalTop = (ActualHeight - LogicalHeight) / 2.0;
        double baseScale = Math.Max(LogicalWidth / source.Width, LogicalHeight / source.Height);
        double imageWidth = source.Width * baseScale;
        double imageHeight = source.Height * baseScale;
        Rect sourceRect = new(
            logicalLeft + LogicalWidth - imageWidth,
            logicalTop + ((LogicalHeight - imageHeight) / 2.0),
            imageWidth,
            imageHeight);
        double logicalCenterX = logicalLeft + (LogicalWidth / 2.0);
        double logicalCenterY = logicalTop + (LogicalHeight / 2.0);

        drawingContext.PushClip(new RectangleGeometry(
            new Rect(0, 0, ActualWidth, ActualHeight),
            CornerRadius,
            CornerRadius));
        drawingContext.PushOpacity(ImageOpacity);
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(ImageScale, ImageScale, logicalCenterX, logicalCenterY));
        transform.Children.Add(new TranslateTransform(
            ImageOffsetX * LogicalWidth,
            ImageOffsetY * LogicalHeight));
        drawingContext.PushTransform(transform);
        drawingContext.DrawImage(source, sourceRect);
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
