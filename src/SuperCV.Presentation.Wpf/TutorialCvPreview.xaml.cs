using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SuperCV;

public partial class TutorialCvPreview : UserControl
{
    public static readonly DependencyProperty PreviewTextProperty = DependencyProperty.Register(
        nameof(PreviewText), typeof(string), typeof(TutorialCvPreview),
        new PropertyMetadata("这是已保存的剪贴板内容。单击条目即可粘贴。"));

    public static readonly DependencyProperty MetricsTextProperty = DependencyProperty.Register(
        nameof(MetricsText), typeof(string), typeof(TutorialCvPreview),
        new PropertyMetadata("24 chars"));

    public static readonly DependencyProperty IndexTextProperty = DependencyProperty.Register(
        nameof(IndexText), typeof(string), typeof(TutorialCvPreview),
        new PropertyMetadata("1/32"));

    public static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        nameof(MarkerBrush), typeof(Brush), typeof(TutorialCvPreview),
        new PropertyMetadata(Brushes.Transparent));

    public TutorialCvPreview()
    {
        InitializeComponent();
    }

    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    public string MetricsText
    {
        get => (string)GetValue(MetricsTextProperty);
        set => SetValue(MetricsTextProperty, value);
    }

    public string IndexText
    {
        get => (string)GetValue(IndexTextProperty);
        set => SetValue(IndexTextProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }
}
