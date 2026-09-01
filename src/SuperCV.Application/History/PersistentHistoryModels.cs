using SuperCV.Domain.Clipboard;

namespace SuperCV.Application.History;

public sealed record PersistentHistoryWrite
{
    public PersistentHistoryWrite(DateTimeOffset capturedAtUtc, string text)
    {
        CapturedAtUtc = capturedAtUtc;
        Text = text ?? string.Empty;
    }

    public PersistentHistoryWrite(DateTimeOffset capturedAtUtc, ClipboardPayload payload)
    {
        CapturedAtUtc = capturedAtUtc;
        Payload = payload ?? ClipboardPayload.Empty;
    }

    public DateTimeOffset CapturedAtUtc { get; }
    public string? Text { get; }
    public ClipboardPayload? Payload { get; }

    /// <summary>Expands a compressed payload only at the repository write boundary.</summary>
    public string GetText() => Payload is null ? Text ?? string.Empty : PersistentHistoryCodec.Encode(Payload);

    public PersistentHistoryWrite Snapshot() => Payload is not null
        ? new PersistentHistoryWrite(CapturedAtUtc.ToUniversalTime(), Payload)
        : new PersistentHistoryWrite(CapturedAtUtc.ToUniversalTime(), Text ?? string.Empty);
}

public sealed record PersistentHistoryMatch(
    DateTimeOffset CapturedAtUtc,
    string Text);

public sealed record PersistentHistoryItem(
    long StorageKey,
    long DisplayId,
    string Text,
    DateTimeOffset CapturedAtUtc);

public sealed record PersistentHistoryCursor(
    long CapturedAtUtcTicks,
    long StorageKey,
    long NextDisplayId,
    int TotalCount);

/// <summary>
/// Restricts a persistent-history query to a half-open UTC time interval.
/// </summary>
public sealed record PersistentHistoryDateRange(
    DateTimeOffset? StartInclusiveUtc,
    DateTimeOffset? EndExclusiveUtc)
{
    public void Validate()
    {
        if (StartInclusiveUtc is { } start &&
            EndExclusiveUtc is { } end &&
            start >= end)
        {
            throw new ArgumentException(
                "The history date range start must be earlier than its end.",
                nameof(EndExclusiveUtc));
        }
    }
}

public sealed record PersistentHistoryPage(
    IReadOnlyList<PersistentHistoryItem> Items,
    PersistentHistoryCursor? NextCursor,
    int TotalCount);

public sealed class PersistentHistoryChangedEventArgs : EventArgs
{
    public PersistentHistoryChangedEventArgs(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
    }

    public Guid WorkspaceId { get; }
}
