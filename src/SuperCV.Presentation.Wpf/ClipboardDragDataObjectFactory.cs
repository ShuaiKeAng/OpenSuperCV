using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SuperCV;

internal static class ClipboardDragDataObjectFactory
{
    internal static DataObject? Create(IReadOnlyDictionary<TextFormat, string> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        var dataObject = new DataObject();
        bool hasData = false;

        hasData |= TrySetData(dataObject, formats, TextFormat.Text, DataFormats.Text);
        hasData |= TrySetData(dataObject, formats, TextFormat.UnicodeText, DataFormats.UnicodeText);
        hasData |= TrySetData(dataObject, formats, TextFormat.Html, DataFormats.Html);
        hasData |= TrySetData(dataObject, formats, TextFormat.Rtf, DataFormats.Rtf);
        hasData |= TrySetImageData(dataObject, formats);

        string? primaryText = GetPrimaryPlainText(formats);
        if (!string.IsNullOrEmpty(primaryText))
        {
            dataObject.SetData(DataFormats.StringFormat, primaryText, autoConvert: false);
            hasData = true;
        }

        return hasData ? dataObject : null;
    }

    private static bool TrySetImageData(
        DataObject dataObject,
        IReadOnlyDictionary<TextFormat, string> formats)
    {
        if (!formats.TryGetValue(TextFormat.Image, out string? imageLink) ||
            string.IsNullOrWhiteSpace(imageLink))
        {
            return false;
        }

        try
        {
            string imagePath = Path.GetFullPath(imageLink);
            if (!File.Exists(imagePath))
            {
                return false;
            }

            BitmapFrame frame;
            using (var stream = new FileStream(
                       imagePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    return false;
                }

                frame = BitmapFrame.Create(decoder.Frames[0]);
                frame.Freeze();
            }

            dataObject.SetData(DataFormats.Bitmap, frame, autoConvert: true);
            dataObject.SetData(
                DataFormats.FileDrop,
                new[] { imagePath },
                autoConvert: false);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                FormatException or
                InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySetData(
        DataObject dataObject,
        IReadOnlyDictionary<TextFormat, string> formats,
        TextFormat sourceFormat,
        string windowsFormat)
    {
        if (!formats.TryGetValue(sourceFormat, out string? value) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        dataObject.SetData(windowsFormat, value, autoConvert: false);
        return true;
    }

    private static string? GetPrimaryPlainText(IReadOnlyDictionary<TextFormat, string> formats)
    {
        if (formats.TryGetValue(TextFormat.UnicodeText, out string? unicodeText) &&
            !string.IsNullOrEmpty(unicodeText))
        {
            return unicodeText;
        }

        return formats.TryGetValue(TextFormat.Text, out string? text) && !string.IsNullOrEmpty(text)
            ? text
            : null;
    }
}
