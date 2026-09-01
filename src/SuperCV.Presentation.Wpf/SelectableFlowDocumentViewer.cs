using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace SuperCV;

/// <summary>
/// Displays a bound <see cref="FlowDocument"/> through the native RichTextBox
/// selection engine while keeping the document read-only.
/// </summary>
public sealed class SelectableFlowDocumentViewer : RichTextBox
{
    public static readonly DependencyProperty DocumentSourceProperty =
        DependencyProperty.Register(
            nameof(DocumentSource),
            typeof(FlowDocument),
            typeof(SelectableFlowDocumentViewer),
            new FrameworkPropertyMetadata(null, OnDocumentSourceChanged));

    public SelectableFlowDocumentViewer()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsInactiveSelectionHighlightEnabled = true;
        IsUndoEnabled = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        Cursor = Cursors.IBeam;
    }

    public FlowDocument? DocumentSource
    {
        get => (FlowDocument?)GetValue(DocumentSourceProperty);
        set => SetValue(DocumentSourceProperty, value);
    }

    private static void OnDocumentSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var viewer = (SelectableFlowDocumentViewer)dependencyObject;
        FlowDocument document = eventArgs.NewValue as FlowDocument
            ?? new FlowDocument();

        if (!ReferenceEquals(viewer.Document, document))
        {
            viewer.Document = document;
        }
    }
}
