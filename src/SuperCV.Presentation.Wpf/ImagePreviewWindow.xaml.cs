using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;

namespace SuperCV;

public partial class ImagePreviewWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmSizing = 0x0214;
    private const int ResizeBorderThickness = 16;
    private const int ImageSurfaceInset = 62;
    private const int WszLeft = 1;
    private const int WszRight = 2;
    private const int WszTop = 3;
    private const int WszTopLeft = 4;
    private const int WszTopRight = 5;
    private const int WszBottom = 6;
    private const int WszBottomLeft = 7;
    private const int WszBottomRight = 8;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int HtClient = 1;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private HwndSource? _source;
    private readonly double _imageAspectRatio;

    internal ImagePreviewWindow(ImageSource imageSource)
    {
        ArgumentNullException.ThrowIfNull(imageSource);

        InitializeComponent();
        PreviewImageBrush.ImageSource = imageSource;
        _imageAspectRatio = GetImageAspectRatio(imageSource);
    }

    internal void ShowAt(Rect sourceBounds)
    {
        Show();
        UpdateLayout();

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        UpdateMaximumSize(handle);
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            (int)Math.Round(sourceBounds.X),
            (int)Math.Round(sourceBounds.Y),
            Math.Max(1, (int)Math.Round(sourceBounds.Width)),
            Math.Max(1, (int)Math.Round(sourceBounds.Height)),
            SwpNoOwnerZOrder | SwpNoActivate | SwpShowWindow);
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
        if (_source is not null)
        {
            UpdateMaximumSize(_source.Handle);
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_source is not null)
        {
            UpdateMaximumSize(_source.Handle);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ImageSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can be cancelled when the window is already closing.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        _source = null;
        PreviewImageBrush.ImageSource = null;
        base.OnClosed(e);
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmSizing)
        {
            AdjustSizingBounds(hwnd, (int)wParam, lParam);
            handled = true;
            return new nint(1);
        }

        if (message != WmNcHitTest)
        {
            return nint.Zero;
        }

        if (!GetWindowRect(hwnd, out NativeRect bounds))
        {
            return nint.Zero;
        }

        int cursorX = GetSignedLowWord(lParam);
        int cursorY = GetSignedHighWord(lParam);
        double scale = GetDpiForWindow(hwnd) / 96d;
        int shadowMargin = (int)Math.Ceiling(20 * scale);
        int surfaceLeft = bounds.Left + shadowMargin;
        int surfaceTop = bounds.Top + shadowMargin;
        int surfaceRight = bounds.Right - shadowMargin;
        int surfaceBottom = bounds.Bottom - shadowMargin;
        if (cursorX < surfaceLeft ||
            cursorX >= surfaceRight ||
            cursorY < surfaceTop ||
            cursorY >= surfaceBottom)
        {
            // The outer transparent margin exists only to render the shadow. Treat it as
            // client space so WPF's default border handling cannot resize from the shadow.
            handled = true;
            return new nint(HtClient);
        }

        int resizeBorder = Math.Max(
            ResizeBorderThickness,
            (int)Math.Ceiling(ResizeBorderThickness * scale));
        bool atLeft = cursorX < surfaceLeft + resizeBorder;
        bool atRight = cursorX >= surfaceRight - resizeBorder;
        bool atTop = cursorY < surfaceTop + resizeBorder;
        bool atBottom = cursorY >= surfaceBottom - resizeBorder;

        int hitTest = (atLeft, atRight, atTop, atBottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => 0,
        };
        if (hitTest == 0)
        {
            return nint.Zero;
        }

        handled = true;
        return new nint(hitTest);
    }

    private void AdjustSizingBounds(nint hwnd, int edge, nint boundsPointer)
    {
        if (boundsPointer == nint.Zero)
        {
            return;
        }

        NativeRect bounds = Marshal.PtrToStructure<NativeRect>(boundsPointer);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96d : 1d;
        int horizontalInset = (int)Math.Round(ImageSurfaceInset * scale);
        int verticalInset = horizontalInset;
        int minimumWidth = (int)Math.Ceiling(MinWidth * scale);
        int minimumHeight = (int)Math.Ceiling(MinHeight * scale);
        int maximumWidth = Math.Max(minimumWidth, (int)Math.Floor(MaxWidth * scale));
        int maximumHeight = Math.Max(minimumHeight, (int)Math.Floor(MaxHeight * scale));
        int proposedWidth = bounds.Right - bounds.Left;
        int proposedHeight = bounds.Bottom - bounds.Top;
        (int width, int height) size = edge is WszTop or WszBottom
            ? CreateSizeFromHeight(
                proposedHeight,
                horizontalInset,
                verticalInset,
                minimumWidth,
                maximumWidth,
                minimumHeight,
                maximumHeight)
            : CreateSizeFromWidth(
                proposedWidth,
                horizontalInset,
                verticalInset,
                minimumWidth,
                maximumWidth,
                minimumHeight,
                maximumHeight);

        bool anchorsRight = edge is WszLeft or WszTopLeft or WszBottomLeft;
        bool anchorsBottom = edge is WszTop or WszTopLeft or WszTopRight;
        if (anchorsRight)
        {
            bounds.Left = bounds.Right - size.width;
        }
        else
        {
            bounds.Right = bounds.Left + size.width;
        }

        if (anchorsBottom)
        {
            bounds.Top = bounds.Bottom - size.height;
        }
        else
        {
            bounds.Bottom = bounds.Top + size.height;
        }

        Marshal.StructureToPtr(bounds, boundsPointer, fDeleteOld: false);
    }

    private (int width, int height) CreateSizeFromWidth(
        int proposedWidth,
        int horizontalInset,
        int verticalInset,
        int minimumWidth,
        int maximumWidth,
        int minimumHeight,
        int maximumHeight)
    {
        int width = Math.Clamp(proposedWidth, minimumWidth, maximumWidth);
        int height = HeightForWidth(width, horizontalInset, verticalInset);
        if (height >= minimumHeight && height <= maximumHeight)
        {
            return (width, height);
        }

        height = Math.Clamp(height, minimumHeight, maximumHeight);
        width = WidthForHeight(height, horizontalInset, verticalInset);
        return (Math.Clamp(width, minimumWidth, maximumWidth), height);
    }

    private (int width, int height) CreateSizeFromHeight(
        int proposedHeight,
        int horizontalInset,
        int verticalInset,
        int minimumWidth,
        int maximumWidth,
        int minimumHeight,
        int maximumHeight)
    {
        int height = Math.Clamp(proposedHeight, minimumHeight, maximumHeight);
        int width = WidthForHeight(height, horizontalInset, verticalInset);
        if (width >= minimumWidth && width <= maximumWidth)
        {
            return (width, height);
        }

        width = Math.Clamp(width, minimumWidth, maximumWidth);
        height = HeightForWidth(width, horizontalInset, verticalInset);
        return (width, Math.Clamp(height, minimumHeight, maximumHeight));
    }

    private int HeightForWidth(int width, int horizontalInset, int verticalInset) =>
        verticalInset + Math.Max(
            1,
            (int)Math.Round((width - horizontalInset) / _imageAspectRatio));

    private int WidthForHeight(int height, int horizontalInset, int verticalInset) =>
        horizontalInset + Math.Max(
            1,
            (int)Math.Round((height - verticalInset) * _imageAspectRatio));

    private static double GetImageAspectRatio(ImageSource imageSource)
    {
        if (imageSource is System.Windows.Media.Imaging.BitmapSource bitmap &&
            bitmap.PixelWidth > 0 &&
            bitmap.PixelHeight > 0)
        {
            return (double)bitmap.PixelWidth / bitmap.PixelHeight;
        }

        return imageSource.Width > 0 && imageSource.Height > 0
            ? imageSource.Width / imageSource.Height
            : 1d;
    }

    private void UpdateMaximumSize(nint handle)
    {
        uint dpi = GetDpiForWindow(handle);
        double scale = dpi > 0 ? dpi / 96d : 1d;
        System.Drawing.Rectangle workArea =
            System.Windows.Forms.Screen.FromHandle((IntPtr)handle).WorkingArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width / scale);
        MaxHeight = Math.Max(MinHeight, workArea.Height / scale);
    }

    private static int GetSignedLowWord(nint value) => (short)((long)value & 0xFFFF);

    private static int GetSignedHighWord(nint value) => (short)(((long)value >> 16) & 0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
