using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SuperCV;

/// <summary>
/// A discrete, snap-to-level slider intended for choosing a context capacity.
/// LevelCount controls the number of available positions; Value is zero based.
/// </summary>
public partial class ContextLevelSlider : UserControl
{
    private Thumb? _activeThumb;
    private bool _isDragging;

    public ContextLevelSlider()
    {
        InitializeComponent();
        LevelSlider.ValueChanged += LevelSlider_ValueChanged;
    }

    public static readonly DependencyProperty LevelCountProperty =
        DependencyProperty.Register(
            nameof(LevelCount),
            typeof(int),
            typeof(ContextLevelSlider),
            new FrameworkPropertyMetadata(5, OnLevelCountChanged, CoerceLevelCount));

    private static readonly DependencyPropertyKey MaximumIndexPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(MaximumIndex),
            typeof(int),
            typeof(ContextLevelSlider),
            new PropertyMetadata(4));

    public static readonly DependencyProperty MaximumIndexProperty =
        MaximumIndexPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey SelectedLabelPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(SelectedLabel),
            typeof(string),
            typeof(ContextLevelSlider),
            new PropertyMetadata("1"));

    public static readonly DependencyProperty SelectedLabelProperty =
        SelectedLabelPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(int),
            typeof(ContextLevelSlider),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    public static readonly DependencyProperty LevelLabelsProperty =
        DependencyProperty.Register(
            nameof(LevelLabels),
            typeof(string),
            typeof(ContextLevelSlider),
            new FrameworkPropertyMetadata(string.Empty, OnLevelLabelsChanged));

    /// <summary>Gets or sets the number of evenly spaced selectable positions. Minimum: 2.</summary>
    public int LevelCount
    {
        get => (int)GetValue(LevelCountProperty);
        set => SetValue(LevelCountProperty, value);
    }

    /// <summary>Gets the zero-based maximum value derived from <see cref="LevelCount"/>.</summary>
    public int MaximumIndex => (int)GetValue(MaximumIndexProperty);

    /// <summary>Gets the label corresponding to the selected position.</summary>
    public string SelectedLabel => (string)GetValue(SelectedLabelProperty);

    /// <summary>Gets or sets the selected zero-based position.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Gets or sets the pipe-separated labels shown for the levels, for example: "32K|64K|128K".
    /// Missing labels fall back to one-based position numbers.
    /// </summary>
    public string LevelLabels
    {
        get => (string)GetValue(LevelLabelsProperty);
        set => SetValue(LevelLabelsProperty, value);
    }

    private static object CoerceLevelCount(DependencyObject dependencyObject, object baseValue) =>
        Math.Max(2, (int)baseValue);

    private static object CoerceValue(DependencyObject dependencyObject, object baseValue)
    {
        var control = (ContextLevelSlider)dependencyObject;
        return Math.Clamp((int)baseValue, 0, Math.Max(0, control.LevelCount - 1));
    }

    private static void OnLevelCountChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (ContextLevelSlider)dependencyObject;
        int levelCount = (int)e.NewValue;
        control.SetValue(MaximumIndexPropertyKey, levelCount - 1);
        control.CoerceValue(ValueProperty);
        control.UpdateSelectedLabel();
    }

    private static void OnLevelLabelsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((ContextLevelSlider)dependencyObject).UpdateSelectedLabel();

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((ContextLevelSlider)dependencyObject).UpdateSelectedLabel();

    private void UpdateSelectedLabel() =>
        SetValue(SelectedLabelPropertyKey, GetLabel(Value, LevelLabels));

    private static string GetLabel(int index, string? labels)
    {
        string[] suppliedLabels = string.IsNullOrWhiteSpace(labels)
            ? []
            : labels.Split('|', StringSplitOptions.TrimEntries);
        return index < suppliedLabels.Length && !string.IsNullOrWhiteSpace(suppliedLabels[index])
            ? suppliedLabels[index]
            : (index + 1).ToString();
    }

    private void LevelThumb_Loaded(object sender, RoutedEventArgs e)
    {
        _activeThumb = sender as Thumb;
        if (_activeThumb?.RenderTransform is not ScaleTransform)
        {
            _activeThumb!.RenderTransform = new ScaleTransform(1, 1);
        }
    }

    private void LevelThumb_DragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;

    private void LevelThumb_DragCompleted(object sender, DragCompletedEventArgs e) => _isDragging = false;

    private void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int snappedValue = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
        if (snappedValue != Value)
        {
            SetCurrentValue(ValueProperty, snappedValue);
        }

        if (_isDragging && _activeThumb?.RenderTransform is ScaleTransform transform)
        {
            AnimateSnapFeedback(transform);
        }
    }

    private static void AnimateSnapFeedback(ScaleTransform transform)
    {
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, CreateSnapAnimation());
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, CreateSnapAnimation());
    }

    private static DoubleAnimationUsingKeyFrames CreateSnapAnimation()
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55))));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170))));
        return animation;
    }
}
