using SuperCV.Application.AI;
using SuperCV.Domain.History;

namespace SuperCV;

/// <summary>
/// Maps presentation history items to the application-owned AI context format.
/// Context always reflects the current real list; evicted clipboard content is never retained.
/// </summary>
public static class AIContextWindow
{
    public static string BuildQuestionContext(IList<CVdata> items, int _)
    {
        ArgumentNullException.ThrowIfNull(items);
        ClipboardEntry[] entries = items
            .Where(HasUsableContent)
            .Select(item => item.ToEntry())
            .ToArray();
        return AiContextBuilder.BuildQuestionContext(entries);
    }

    public static string BuildRealItemsText(
        IEnumerable<(CVdata Item, int OriginalIndex)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        IEnumerable<(ClipboardEntry Entry, int OriginalIndex)> entries = items
            .Where(item => HasUsableContent(item.Item))
            .Select(item => (item.Item.ToEntry(), item.OriginalIndex));
        return AiContextBuilder.BuildSearchBatch(entries);
    }

    public static string GetItemText(CVdata? item)
    {
        if (item?.AllTextList is null || item.AllTextList.Count == 0)
        {
            return string.Empty;
        }

        if (item.AllTextList.TryGetValue(TextFormat.UnicodeText, out string? unicodeText)
            && !string.IsNullOrEmpty(unicodeText))
        {
            return unicodeText;
        }

        if (item.AllTextList.TryGetValue(TextFormat.Text, out string? plainText)
            && !string.IsNullOrEmpty(plainText))
        {
            return plainText;
        }

        return string.Join(
            Environment.NewLine,
            item.AllTextList
                .Where(pair => pair.Key != TextFormat.Image)
                .Select(pair => pair.Value)
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal));
    }

    private static bool HasUsableContent(CVdata item) =>
        !string.IsNullOrWhiteSpace(GetItemText(item));
}
