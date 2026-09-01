using System.Windows;
using System.Windows.Controls;

namespace SuperCV;

public partial class TutorialMainWindowPreview : UserControl
{
    public static readonly DependencyProperty IsDetailedProperty = DependencyProperty.Register(
        nameof(IsDetailed), typeof(bool), typeof(TutorialMainWindowPreview),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsSearchExpandedProperty = DependencyProperty.Register(
        nameof(IsSearchExpanded), typeof(bool), typeof(TutorialMainWindowPreview),
        new PropertyMetadata(false));

    public static readonly DependencyProperty SearchPreviewTextProperty = DependencyProperty.Register(
        nameof(SearchPreviewText), typeof(string), typeof(TutorialMainWindowPreview),
        new PropertyMetadata("搜索"));

    public static readonly DependencyProperty WorkspacePreviewTextProperty = DependencyProperty.Register(
        nameof(WorkspacePreviewText), typeof(string), typeof(TutorialMainWindowPreview),
        new PropertyMetadata("默认工作区"));

    public TutorialMainWindowPreview()
    {
        InitializeComponent();
        // Default dependency-property values are not local values, so the runtime localizer
        // cannot preserve and translate them.  Store the preview-only source text locally.
        SetValue(SearchPreviewTextProperty, "搜索");
        SetValue(WorkspacePreviewTextProperty, "默认工作区");
    }

    public bool IsDetailed
    {
        get => (bool)GetValue(IsDetailedProperty);
        set => SetValue(IsDetailedProperty, value);
    }

    public bool IsSearchExpanded
    {
        get => (bool)GetValue(IsSearchExpandedProperty);
        set => SetValue(IsSearchExpandedProperty, value);
    }

    public string SearchPreviewText
    {
        get => (string)GetValue(SearchPreviewTextProperty);
        set => SetValue(SearchPreviewTextProperty, value);
    }

    public string WorkspacePreviewText
    {
        get => (string)GetValue(WorkspacePreviewTextProperty);
        set => SetValue(WorkspacePreviewTextProperty, value);
    }
}
