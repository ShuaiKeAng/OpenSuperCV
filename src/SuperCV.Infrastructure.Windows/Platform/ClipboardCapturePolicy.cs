using System.Net;
using System.Text.RegularExpressions;
using SuperCV.Domain.Clipboard;

namespace SuperCV.Infrastructure.Windows.Platform;

internal static partial class ClipboardCapturePolicy
{
    private const string StartFragmentMarker = "<!--StartFragment-->";
    private const string EndFragmentMarker = "<!--EndFragment-->";

    internal static bool ShouldPreferImage(
        IReadOnlyDictionary<ClipboardFormat, string> textFormats)
    {
        ArgumentNullException.ThrowIfNull(textFormats);
        if (textFormats.Count == 0)
        {
            return true;
        }

        string[] plainTextValues = textFormats
            .Where(pair =>
                pair.Key is ClipboardFormat.UnicodeText or ClipboardFormat.Text)
            .Select(pair => pair.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (plainTextValues.Length > 0 &&
            plainTextValues.All(IsImagePlaceholder))
        {
            return true;
        }

        return plainTextValues.Length == 0 &&
               textFormats.TryGetValue(ClipboardFormat.Html, out string? html) &&
               IsImageOnlyHtml(html) &&
               textFormats.Keys.All(format => format == ClipboardFormat.Html);
    }

    internal static bool IsImageOnlyHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) ||
            html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        ReadOnlySpan<char> fragment = ExtractFragment(html);
        string visibleText = WebUtility.HtmlDecode(
            HtmlTagRegex().Replace(fragment.ToString(), string.Empty));
        return string.IsNullOrWhiteSpace(visibleText);
    }

    private static ReadOnlySpan<char> ExtractFragment(string html)
    {
        int start = html.IndexOf(StartFragmentMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += StartFragmentMarker.Length;
            int end = html.IndexOf(
                EndFragmentMarker,
                start,
                StringComparison.OrdinalIgnoreCase);
            if (end >= start)
            {
                return html.AsSpan(start, end - start);
            }
        }

        int htmlStart = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        return htmlStart >= 0 ? html.AsSpan(htmlStart) : html.AsSpan();
    }

    private static bool IsImagePlaceholder(string value) =>
        value.Equals("[图片]", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("【图片】", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("[image]", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        "<!--.*?-->|<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();
}
