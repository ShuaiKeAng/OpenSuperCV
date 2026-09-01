namespace SuperCV;

internal sealed class ClipboardShortcutNavigator
{
    private Guid? _anchorId;

    internal Guid? AnchorId => _anchorId;

    internal void Record(Guid entryId)
    {
        _anchorId = entryId == Guid.Empty ? null : entryId;
    }

    internal int? Select(IReadOnlyList<Guid> entries, int index)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (index < 0 || index >= entries.Count)
        {
            return null;
        }

        _anchorId = entries[index];
        return index;
    }

    internal int? MoveOlder(IReadOnlyList<Guid> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            _anchorId = null;
            return null;
        }

        int anchorIndex = FindAnchorIndex(entries);
        int targetIndex = anchorIndex < 0 ? 0 : anchorIndex + 1;
        if (targetIndex >= entries.Count)
        {
            return null;
        }

        _anchorId = entries[targetIndex];
        return targetIndex;
    }

    internal int? MoveNewer(IReadOnlyList<Guid> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            _anchorId = null;
            return null;
        }

        int anchorIndex = FindAnchorIndex(entries);
        int targetIndex = anchorIndex < 0 ? 0 : anchorIndex - 1;
        if (targetIndex < 0)
        {
            return null;
        }

        _anchorId = entries[targetIndex];
        return targetIndex;
    }

    private int FindAnchorIndex(IReadOnlyList<Guid> entries)
    {
        if (_anchorId is not Guid anchorId)
        {
            return -1;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index] == anchorId)
            {
                return index;
            }
        }

        return -1;
    }
}
