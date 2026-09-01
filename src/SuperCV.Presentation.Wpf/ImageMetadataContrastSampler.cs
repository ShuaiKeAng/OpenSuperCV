using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SuperCV;

internal static class ImageMetadataContrastSampler
{
    internal static (byte Metrics, byte LaunchTime, byte Index)? TrySample(BitmapSource source)
    {
        try
        {
            BitmapSource sampleSource = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            const double renderedWidth = 233d;
            double renderedHeight =
                renderedWidth * sampleSource.PixelHeight / sampleSource.PixelWidth;
            var renderedSize = new Size(renderedWidth, renderedHeight);
            double sampleHeight = Math.Min(17d, renderedHeight);
            double sampleTop = Math.Min(81d, renderedHeight - sampleHeight);
            return (
                GetContrastingGray(
                    sampleSource,
                    new Rect(8d, sampleTop, 68d, sampleHeight),
                    renderedSize),
                GetContrastingGray(
                    sampleSource,
                    new Rect(84d, sampleTop, 51d, sampleHeight),
                    renderedSize),
                GetContrastingGray(
                    sampleSource,
                    new Rect(168d, sampleTop, 61d, sampleHeight),
                    renderedSize));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                OverflowException)
        {
            return null;
        }
    }

    internal static byte GetContrastingGray(
        BitmapSource source,
        Rect targetBounds,
        Size renderedImageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 ||
            source.PixelHeight <= 0 ||
            renderedImageSize.Width <= 0 ||
            renderedImageSize.Height <= 0)
        {
            return 235;
        }

        targetBounds.Inflate(2, 2);
        Rect sampleBounds = Rect.Intersect(
            targetBounds,
            new Rect(new Point(), renderedImageSize));
        if (sampleBounds.IsEmpty)
        {
            return 235;
        }

        int left = Math.Clamp(
            (int)(sampleBounds.Left * source.PixelWidth / renderedImageSize.Width),
            0,
            source.PixelWidth - 1);
        int top = Math.Clamp(
            (int)(sampleBounds.Top * source.PixelHeight / renderedImageSize.Height),
            0,
            source.PixelHeight - 1);
        int right = Math.Clamp(
            (int)Math.Ceiling(sampleBounds.Right * source.PixelWidth / renderedImageSize.Width),
            left + 1,
            source.PixelWidth);
        int bottom = Math.Clamp(
            (int)Math.Ceiling(sampleBounds.Bottom * source.PixelHeight / renderedImageSize.Height),
            top + 1,
            source.PixelHeight);
        var pixelBounds = new Int32Rect(left, top, right - left, bottom - top);

        BitmapSource bgraSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = pixelBounds.Width * 4;
        byte[] pixels = new byte[stride * pixelBounds.Height];
        bgraSource.CopyPixels(pixelBounds, pixels, stride, 0);

        double luminance = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            double alpha = pixels[index + 3] / 255d;
            double opaqueLuminance =
                (0.0722d * pixels[index]) +
                (0.7152d * pixels[index + 1]) +
                (0.2126d * pixels[index + 2]);
            luminance += (opaqueLuminance * alpha) + (127d * (1d - alpha));
        }

        double averageLuminance = luminance / (pixels.Length / 4);
        return averageLuminance >= 128d ? (byte)20 : (byte)235;
    }
}
