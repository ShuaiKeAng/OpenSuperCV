using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuperCV;

/// <summary>
/// Presents the existing application artwork at the visual size expected by Windows icon slots.
/// The source assets remain unchanged; only their transparent outer canvas is tightened in memory.
/// </summary>
internal static class ApplicationIconRenderer
{
    private const string IconResourcePath =
        "pack://application:,,,/SuperCV;component/Assets/SuperCV.Remastered.png";
    private const byte VisibleAlphaThreshold = 8;
    private const double CropSafetyMarginRatio = 0.025;
    private const int TrayIconSize = 32;
    private const int TrayEdgeInset = 1;

    internal static ImageSource? TryCreateTaskbarImageSource()
    {
        try
        {
            BitmapSource source = LoadSource();
            BitmapSource cropped = CropTransparentCanvas(source);
            cropped.Freeze();
            return cropped;
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static System.Drawing.Icon? TryCreateTrayIcon()
    {
        try
        {
            BitmapSource source = CropTransparentCanvas(LoadSource());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            encoded.Position = 0;

            using var sourceBitmap = new System.Drawing.Bitmap(encoded);
            using var targetBitmap = new System.Drawing.Bitmap(
                TrayIconSize,
                TrayIconSize,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (System.Drawing.Graphics graphics =
                   System.Drawing.Graphics.FromImage(targetBitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.CompositingMode =
                    System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.CompositingQuality =
                    System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode =
                    System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.SmoothingMode =
                    System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                int renderedSize = TrayIconSize - (TrayEdgeInset * 2);
                graphics.DrawImage(
                    sourceBitmap,
                    new System.Drawing.Rectangle(
                        TrayEdgeInset,
                        TrayEdgeInset,
                        renderedSize,
                        renderedSize));
            }

            nint iconHandle = targetBitmap.GetHicon();
            try
            {
                using System.Drawing.Icon borrowed =
                    System.Drawing.Icon.FromHandle(iconHandle);
                return (System.Drawing.Icon)borrowed.Clone();
            }
            finally
            {
                _ = DestroyIcon(iconHandle);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException
            or ExternalException)
        {
            return null;
        }
    }

    private static BitmapSource LoadSource()
    {
        Uri resourceUri = new(IconResourcePath, UriKind.Absolute);
        System.Windows.Resources.StreamResourceInfo? resource =
            System.Windows.Application.GetResourceStream(resourceUri);
        if (resource is null)
        {
            throw new IOException(
                $"Application icon resource '{IconResourcePath}' was not found.");
        }

        using (resource.Stream)
        {
            return BitmapFrame.Create(
                resource.Stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
        }
    }

    private static BitmapSource CropTransparentCanvas(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgra32,
            null,
            0);
        int stride = checked(formatted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * formatted.PixelHeight)];
        formatted.CopyPixels(pixels, stride, 0);

        int left = formatted.PixelWidth;
        int top = formatted.PixelHeight;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < formatted.PixelHeight; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < formatted.PixelWidth; x++)
            {
                if (pixels[rowStart + (x * 4) + 3] <= VisibleAlphaThreshold)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return source;
        }

        int visibleWidth = right - left + 1;
        int visibleHeight = bottom - top + 1;
        int safetyMargin = Math.Max(
            1,
            (int)Math.Ceiling(
                Math.Max(visibleWidth, visibleHeight) * CropSafetyMarginRatio));
        left = Math.Max(0, left - safetyMargin);
        top = Math.Max(0, top - safetyMargin);
        right = Math.Min(formatted.PixelWidth - 1, right + safetyMargin);
        bottom = Math.Min(formatted.PixelHeight - 1, bottom + safetyMargin);

        return new CroppedBitmap(
            source,
            new Int32Rect(
                left,
                top,
                right - left + 1,
                bottom - top + 1));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
