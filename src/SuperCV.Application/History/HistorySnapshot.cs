using SuperCV.Domain.History;

namespace SuperCV.Application.History;

public sealed record HistorySnapshot(
    IReadOnlyList<ClipboardEntry> Entries,
    IReadOnlyList<ClipboardEntry> FilteredEntries,
    IReadOnlyList<ClipboardEntry> VisibleEntries,
    int StartIndex,
    int PageSize,
    bool IsFiltered,
    bool IsAtTop,
    bool IsAtBottom,
    bool ExceedsLimitBecauseAllOverflowItemsArePinned);

public sealed class HistoryChangedEventArgs : EventArgs
{
    public HistoryChangedEventArgs(HistorySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public HistorySnapshot Snapshot { get; }
}
