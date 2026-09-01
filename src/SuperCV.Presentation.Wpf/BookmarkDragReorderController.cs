using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SuperCV;

internal sealed class BookmarkDragReorderController : IDisposable
{
    private static readonly TimeSpan ShiftDuration = TimeSpan.FromMilliseconds(90);

    private readonly ItemsControl _itemsControl;
    private readonly Func<Guid, int, ValueTask<bool>> _moveAsync;
    private readonly Action<Exception> _reportFailure;

    private CapsuleButton? _candidate;
    private CapsuleButton? _dragged;
    private Point _pressPoint;
    private int _originIndex = -1;
    private int _targetIndex = -1;
    private CapsuleButton[] _buttons = [];
    private BookmarkDragSlot[] _slots = [];
    private OriginalVisualState[] _originalVisuals = [];
    private TranslateTransform?[] _siblingTranslations = [];
    private TranslateTransform? _dragTranslation;
    private ScrollViewer? _scrollViewer;
    private bool _isCommitting;
    private bool _disposed;

    public BookmarkDragReorderController(
        ItemsControl itemsControl,
        Func<Guid, int, ValueTask<bool>> moveAsync,
        Action<Exception> reportFailure)
    {
        _itemsControl = itemsControl ?? throw new ArgumentNullException(nameof(itemsControl));
        _moveAsync = moveAsync ?? throw new ArgumentNullException(nameof(moveAsync));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));

        _itemsControl.AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown),
            handledEventsToo: true);
        _itemsControl.AddHandler(
            Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPreviewMouseMove),
            handledEventsToo: true);
        _itemsControl.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnPreviewMouseUp),
            handledEventsToo: true);
        _itemsControl.AddHandler(
            Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnLostMouseCapture),
            handledEventsToo: true);
    }

    public bool IsDragging => _dragged is not null;

    public void HandleCollectionChanging()
    {
        if (IsDragging && !_isCommitting)
        {
            CancelDrag();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDrag();
        _itemsControl.RemoveHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown));
        _itemsControl.RemoveHandler(
            Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPreviewMouseMove));
        _itemsControl.RemoveHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnPreviewMouseUp));
        _itemsControl.RemoveHandler(
            Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnLostMouseCapture));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        CapsuleButton? button = FindVisualAncestor<CapsuleButton>(source, _itemsControl);
        if (_disposed ||
            _isCommitting ||
            e.ChangedButton != MouseButton.Left ||
            e.LeftButton != MouseButtonState.Pressed ||
            button is null ||
            IsButtonSource(source, button))
        {
            return;
        }

        int index = _itemsControl.Items.IndexOf(button);
        if (index < 0)
        {
            return;
        }

        _candidate = button;
        _originIndex = index;
        _targetIndex = index;
        _pressPoint = e.GetPosition(_itemsControl);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_disposed || (_candidate is null && _dragged is null))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragged is not null)
            {
                CancelDrag();
            }
            else
            {
                ResetCandidate();
            }

            return;
        }

        Point point = e.GetPosition(_itemsControl);
        if (_dragged is null)
        {
            if (!PassedDragThreshold(point))
            {
                return;
            }

            BeginDrag(_candidate!);
        }

        AutoScroll(e);
        UpdatePreview(e.GetPosition(_itemsControl));
        e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_dragged is not null)
        {
            e.Handled = true;
            CommitPreviewedOrder();
        }
        else if (_candidate is not null)
        {
            ResetCandidate();
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isCommitting && _dragged is { IsMouseCaptured: false })
        {
            CancelDrag();
        }
    }

    private bool PassedDragThreshold(Point point) =>
        Math.Abs(point.X - _pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(point.Y - _pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void BeginDrag(CapsuleButton button)
    {
        _dragged = button;
        _candidate = null;
        button.CancelPendingPointerActions();

        PrepareVisuals(button);
        _scrollViewer = FindVisualAncestor<ScrollViewer>(_itemsControl);
        Panel.SetZIndex(GetContainer(button), 1000);
        button.Cursor = Cursors.SizeAll;
        button.Opacity = 0.92;

        if (!button.CaptureMouse())
        {
            ResetDragState();
        }
    }

    private void PrepareVisuals(CapsuleButton dragged)
    {
        _buttons = GetButtons();
        _slots = new BookmarkDragSlot[_buttons.Length];
        _originalVisuals = new OriginalVisualState[_buttons.Length];
        _siblingTranslations = new TranslateTransform?[_buttons.Length];
        _originIndex = Array.IndexOf(_buttons, dragged);
        _targetIndex = _originIndex;

        for (int index = 0; index < _buttons.Length; index++)
        {
            CapsuleButton button = _buttons[index];
            Point topLeft = button.TranslatePoint(new Point(0, 0), _itemsControl);
            _slots[index] = new BookmarkDragSlot(topLeft.X, topLeft.X + button.ActualWidth);
            _originalVisuals[index] = new OriginalVisualState(
                button.RenderTransform,
                button.RenderTransformOrigin,
                Panel.GetZIndex(GetContainer(button)),
                button.Opacity);

            button.RenderTransformOrigin = new Point(0.5, 0.5);
            if (ReferenceEquals(button, dragged))
            {
                _dragTranslation = new TranslateTransform();
                var transform = new TransformGroup();
                transform.Children.Add(new ScaleTransform(1.04, 1.04));
                transform.Children.Add(_dragTranslation);
                button.RenderTransform = transform;
            }
            else
            {
                var translation = new TranslateTransform();
                _siblingTranslations[index] = translation;
                button.RenderTransform = translation;
            }
        }
    }

    private void UpdatePreview(Point point)
    {
        if (_dragTranslation is null)
        {
            return;
        }

        double deltaX = point.X - _pressPoint.X;
        DpiScale dpi = VisualTreeHelper.GetDpi(_itemsControl);
        _dragTranslation.X = PixelAlignedWindowMotion.AlignLogicalCoordinate(
            deltaX,
            dpi.DpiScaleX);
        _dragTranslation.Y = PixelAlignedWindowMotion.AlignLogicalCoordinate(
            Math.Clamp(point.Y - _pressPoint.Y, -7.0, 7.0),
            dpi.DpiScaleY);

        int targetIndex = CalculateOverlappedSlotIndex(_originIndex, deltaX, _slots);
        if (targetIndex == _targetIndex)
        {
            return;
        }

        _targetIndex = targetIndex;
        UpdateSiblingPreview();
    }

    private void UpdateSiblingPreview()
    {
        double draggedWidth = GetSlotWidth(_dragged!);
        double dpiScale = VisualTreeHelper.GetDpi(_itemsControl).DpiScaleX;
        for (int index = 0; index < _buttons.Length; index++)
        {
            TranslateTransform? translation = _siblingTranslations[index];
            if (translation is null)
            {
                continue;
            }

            double offset = 0;
            if (_targetIndex > _originIndex && index > _originIndex && index <= _targetIndex)
            {
                offset = -draggedWidth;
            }
            else if (_targetIndex < _originIndex && index >= _targetIndex && index < _originIndex)
            {
                offset = draggedWidth;
            }

            AnimateTranslation(translation, offset, dpiScale);
        }
    }

    private void CommitPreviewedOrder()
    {
        CapsuleButton? droppedButton = _dragged;
        if (droppedButton is null)
        {
            return;
        }

        int originIndex = _originIndex;
        int targetIndex = _targetIndex;
        _isCommitting = true;
        if (droppedButton.IsMouseCaptured)
        {
            droppedButton.ReleaseMouseCapture();
        }

        _ = CommitPreviewedOrderAsync(droppedButton, originIndex, targetIndex);
    }

    private async Task CommitPreviewedOrderAsync(
        CapsuleButton droppedButton,
        int originIndex,
        int targetIndex)
    {
        try
        {
            if (droppedButton.Tag is Guid id && targetIndex != originIndex)
            {
                await _moveAsync(id, targetIndex);
            }
        }
        catch (Exception exception)
        {
            _reportFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(_dragged, droppedButton))
            {
                ResetDragState();
            }
        }
    }

    private void CancelDrag()
    {
        ResetCandidate();
        if (_dragged is null)
        {
            return;
        }

        _isCommitting = true;
        if (_dragged.IsMouseCaptured)
        {
            _dragged.ReleaseMouseCapture();
        }

        ResetDragState();
    }

    private void ResetDragState()
    {
        for (int index = 0; index < _buttons.Length; index++)
        {
            CapsuleButton button = _buttons[index];
            OriginalVisualState state = _originalVisuals[index];
            button.RenderTransform = state.Transform;
            button.RenderTransformOrigin = state.Origin;
            button.Opacity = state.Opacity;
            Panel.SetZIndex(GetContainer(button), state.ZIndex);
            button.Cursor = Cursors.Hand;
        }

        _candidate = null;
        _dragged = null;
        _buttons = [];
        _slots = [];
        _originalVisuals = [];
        _siblingTranslations = [];
        _dragTranslation = null;
        _scrollViewer = null;
        _originIndex = -1;
        _targetIndex = -1;
        _isCommitting = false;
    }

    private void ResetCandidate()
    {
        _candidate = null;
        if (_dragged is null)
        {
            _originIndex = -1;
            _targetIndex = -1;
        }
    }

    private void AutoScroll(MouseEventArgs e)
    {
        if (_scrollViewer is null || _scrollViewer.ViewportWidth <= 0)
        {
            return;
        }

        Point point = e.GetPosition(_scrollViewer);
        const double edge = 24;
        const double step = 10;
        if (point.X < edge)
        {
            _scrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, _scrollViewer.HorizontalOffset - step));
        }
        else if (point.X > _scrollViewer.ViewportWidth - edge)
        {
            _scrollViewer.ScrollToHorizontalOffset(
                Math.Min(_scrollViewer.ScrollableWidth, _scrollViewer.HorizontalOffset + step));
        }
    }

    internal static int CalculateOverlappedSlotIndex(
        int originIndex,
        double deltaX,
        IReadOnlyList<BookmarkDragSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if ((uint)originIndex >= (uint)slots.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(originIndex));
        }

        int targetIndex = originIndex;
        BookmarkDragSlot draggedSlot = slots[originIndex];
        if (deltaX > 0)
        {
            double draggedRight = draggedSlot.Right + deltaX;
            for (int index = originIndex + 1; index < slots.Count; index++)
            {
                if (draggedRight < slots[index].Left)
                {
                    break;
                }

                targetIndex = index;
            }
        }
        else if (deltaX < 0)
        {
            double draggedLeft = draggedSlot.Left + deltaX;
            for (int index = originIndex - 1; index >= 0; index--)
            {
                if (draggedLeft > slots[index].Right)
                {
                    break;
                }

                targetIndex = index;
            }
        }

        return targetIndex;
    }

    private CapsuleButton[] GetButtons() =>
        _itemsControl.Items.OfType<CapsuleButton>().ToArray();

    private UIElement GetContainer(CapsuleButton button) =>
        _itemsControl.ItemContainerGenerator.ContainerFromItem(button) as UIElement ?? button;

    private static double GetSlotWidth(CapsuleButton button) =>
        button.ActualWidth + button.Margin.Left + button.Margin.Right;

    private static void AnimateTranslation(
        TranslateTransform translation,
        double value,
        double dpiScale)
    {
        double current = PixelAlignedWindowMotion.AlignLogicalCoordinate(
            translation.X,
            dpiScale);
        double target = PixelAlignedWindowMotion.AlignLogicalCoordinate(
            value,
            dpiScale);
        translation.BeginAnimation(TranslateTransform.XProperty, null);
        translation.X = target;
        var animation = new PixelAlignedDoubleAnimation(
            current,
            target,
            dpiScale,
            ShiftDuration)
        {
            FillBehavior = FillBehavior.Stop,
        };
        translation.BeginAnimation(
            TranslateTransform.XProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static bool IsButtonSource(DependencyObject? source, DependencyObject root)
    {
        for (DependencyObject? current = source;
             current is not null && !ReferenceEquals(current, root);
             current = GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(
        DependencyObject? start,
        DependencyObject? stop = null)
        where T : DependencyObject
    {
        for (DependencyObject? current = start; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }

            if (ReferenceEquals(current, stop))
            {
                break;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current is Visual
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private readonly record struct OriginalVisualState(
        Transform Transform,
        Point Origin,
        int ZIndex,
        double Opacity);

    internal readonly record struct BookmarkDragSlot(double Left, double Right);
}
