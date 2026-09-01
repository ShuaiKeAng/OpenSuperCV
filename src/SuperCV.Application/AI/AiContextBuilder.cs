using System.Text;
using System.Text.Json;
using SuperCV.Domain.History;

namespace SuperCV.Application.AI;

public static class AiContextBuilder
{
    public static string BuildQuestionContext(IReadOnlyList<ClipboardEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        builder.AppendLine("【真实条目：只能根据本 JSON Lines 区域回答】");
        AppendEntries(
            builder,
            entries.Select((entry, index) => (entry, index)),
            includeCreatedAt: false);
        return builder.ToString();
    }

    public static string BuildSearchBatch(
        IEnumerable<(ClipboardEntry Entry, int OriginalIndex)> entries,
        int maxContentCharacters = int.MaxValue,
        Func<ClipboardEntry, string>? contentProvider = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maxContentCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContentCharacters));
        }

        var builder = new StringBuilder();
        AppendEntries(
            builder,
            entries.Select(item => (item.Entry, item.OriginalIndex)),
            maxContentCharacters,
            includeCreatedAt: true,
            contentProvider);
        return builder.ToString();
    }

    private static void AppendEntries(
        StringBuilder builder,
        IEnumerable<(ClipboardEntry Entry, int Index)> entries,
        int maxContentCharacters = int.MaxValue,
        bool includeCreatedAt = false,
        Func<ClipboardEntry, string>? contentProvider = null)
    {
        foreach ((ClipboardEntry entry, int index) in entries)
        {
            string content = contentProvider?.Invoke(entry) ?? entry.Payload.PrimaryText;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            content = Truncate(content, maxContentCharacters);

            string id = $"R{index}";
            builder.AppendLine(includeCreatedAt
                ? JsonSerializer.Serialize(new SearchRealItem(
                    Id: id,
                    CreatedAt: entry.CapturedAtUtc.ToLocalTime(),
                    Length: content.Length,
                    Content: content))
                : JsonSerializer.Serialize(new RealItem(
                    Id: id,
                    Length: content.Length,
                    Content: content)));
        }
    }

    private static string Truncate(string content, int maxCharacters)
    {
        if (content.Length <= maxCharacters)
        {
            return content;
        }

        int length = maxCharacters;
        if (length > 0 &&
            char.IsHighSurrogate(content[length - 1]) &&
            char.IsLowSurrogate(content[length]))
        {
            length--;
        }

        return content[..length];
    }

    private sealed record RealItem(string Id, int Length, string Content);

    private sealed record SearchRealItem(
        string Id,
        DateTimeOffset CreatedAt,
        int Length,
        string Content);
}
