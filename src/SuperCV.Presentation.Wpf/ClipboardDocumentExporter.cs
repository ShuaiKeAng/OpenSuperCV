using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SuperCV;

internal static partial class ClipboardDocumentExporter
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true);

    internal static bool ShowExportDialog(
        Window owner,
        IReadOnlyDictionary<TextFormat, string> formats,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(formats);

        bool isImage = TryGetValue(formats, TextFormat.Image, out _);
        var dialog = new SaveFileDialog
        {
            Title = "导出",
            FileName = $"SuperCV_{capturedAtUtc.ToLocalTime():yyyyMMdd_HHmmss}",
            DefaultExt = string.Empty,
            Filter = isImage
                ? "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP 图片 (*.bmp)|*.bmp|TIFF 图片 (*.tif;*.tiff)|*.tif;*.tiff"
                : "文本文档 (*.txt)|*.txt|Word 富文本文档 (*.rtf)|*.rtf",
            FilterIndex = 1,
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            RestoreDirectory = true,
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return false;
        }

        WriteExport(dialog.FileName, formats);
        return true;
    }

    internal static void WriteExport(
        string filePath,
        IReadOnlyDictionary<TextFormat, string> formats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(formats);

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (TryGetValue(formats, TextFormat.Image, out string imagePath))
        {
            WriteImage(filePath, imagePath, extension);
            return;
        }

        switch (extension)
        {
            case ".txt":
                File.WriteAllText(filePath, GetPlainText(formats), Utf8WithBom);
                break;
            case ".rtf":
                File.WriteAllText(filePath, GetRichText(formats), Encoding.ASCII);
                break;
            default:
                throw new NotSupportedException("仅支持导出 TXT 或 RTF 文档。");
        }
    }

    internal static void WriteDocument(
        string filePath,
        IReadOnlyDictionary<TextFormat, string> formats) =>
        WriteExport(filePath, formats);

    internal static string GetPlainText(IReadOnlyDictionary<TextFormat, string> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        if (TryGetValue(formats, TextFormat.UnicodeText, out string unicodeText))
        {
            return unicodeText;
        }

        if (TryGetValue(formats, TextFormat.Text, out string text))
        {
            return text;
        }

        if (TryGetValue(formats, TextFormat.Rtf, out string rtf))
        {
            string? extractedText = TryExtractRtfText(rtf);
            if (extractedText is not null)
            {
                return extractedText;
            }
        }

        return TryGetValue(formats, TextFormat.Html, out string html)
            ? ExtractHtmlText(html)
            : string.Empty;
    }

    internal static string GetRichText(IReadOnlyDictionary<TextFormat, string> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);

        if (TryGetValue(formats, TextFormat.Rtf, out string rtf) &&
            rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return rtf;
        }

        return CreateRtf(GetPlainText(formats));
    }

    private static void WriteImage(
        string filePath,
        string imagePath,
        string extension)
    {
        BitmapEncoder encoder = extension switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder
            {
                Compression = TiffCompressOption.Zip,
            },
            _ => throw new NotSupportedException(
                "仅支持导出 PNG、JPEG、BMP 或 TIFF 图片。"),
        };

        string sourcePath = Path.GetFullPath(imagePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("原始图片已不可用，无法导出。", sourcePath);
        }

        BitmapFrame frame;
        using (var input = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("原始图片没有可导出的画面。");
            }

            frame = BitmapFrame.Create(decoder.Frames[0]);
            frame.Freeze();
        }

        encoder.Frames.Add(frame);
        using var output = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        encoder.Save(output);
    }

    private static bool TryGetValue(
        IReadOnlyDictionary<TextFormat, string> formats,
        TextFormat format,
        out string value)
    {
        if (formats.TryGetValue(format, out string? candidate) &&
            !string.IsNullOrEmpty(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? TryExtractRtfText(string rtf)
    {
        try
        {
            var document = new FlowDocument();
            var range = new TextRange(document.ContentStart, document.ContentEnd);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
            range.Load(stream, DataFormats.Rtf);
            return range.Text.TrimEnd('\r', '\n');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ExtractHtmlText(string html)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";

        int startIndex = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int endIndex = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        string fragment = startIndex >= 0 && endIndex > startIndex
            ? html[(startIndex + startMarker.Length)..endIndex]
            : html;

        fragment = HtmlLineBreakRegex().Replace(fragment, Environment.NewLine);
        fragment = HtmlTagRegex().Replace(fragment, string.Empty);
        return WebUtility.HtmlDecode(fragment).Trim();
    }

    private static string CreateRtf(string text)
    {
        var builder = new StringBuilder(
            @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\viewkind4\uc1\f0\fs22 ");

        foreach (char character in text)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '{':
                    builder.Append(@"\{");
                    break;
                case '}':
                    builder.Append(@"\}");
                    break;
                case '\r':
                    break;
                case '\n':
                    builder.Append(@"\par ");
                    break;
                case '\t':
                    builder.Append(@"\tab ");
                    break;
                default:
                    if (character is >= ' ' and <= '~')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append(@"\u");
                        builder.Append(unchecked((short)character));
                        builder.Append('?');
                    }

                    break;
            }
        }

        builder.Append('}');
        return builder.ToString();
    }

    [GeneratedRegex(
        @"<(?:br\s*/?|/p|/div|/li|/tr|/h[1-6])\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlLineBreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();
}
