using SuperCV.Application.Ports;
using SuperCV.Domain.Clipboard;
using SuperCV.Domain.History;
using SuperCV.Domain.Workspaces;

namespace SuperCV.Application.History;

public sealed class HistoryService : IAsyncDisposable
{
    public const string BlankEntryDefaultText = "右键编辑输入文本";

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly ClipboardPayload BlankEntryPayload = new(
        new Dictionary<ClipboardFormat, string>
        {
            [ClipboardFormat.UnicodeText] = BlankEntryDefaultText,
        });
    private readonly object _gate = new();
    private readonly SemaphoreSlim _workspaceGate = new(1, 1);
    private readonly IHistoryRepository _repository;
    private readonly PersistentHistoryService? _persistentHistory;
    private readonly TimeProvider _timeProvider;
    private readonly CoalescingSaveQueue<WorkspaceHistorySave> _saveQueue;
    private List<ClipboardEntry> _entries = new();
    private Guid _activeWorkspaceId;
    private HashSet<Guid>? _externalFilter;
    private string _searchText = string.Empty;
    // A deferred payload loads its body from an individual JSON file.  Keep the result set,
    // not the bodies, while the source list and filter are unchanged so paging a search does
    // not rescan every item on every wheel step.
    private List<ClipboardEntry>? _filteredEntriesCache;
    private ClipboardEntry[]? _filteredEntriesCacheSource;
    private HashSet<Guid>? _filteredEntriesCacheExternalFilter;
    private string? _filteredEntriesCacheSearchText;
    private int _startIndex;
    private int _pageSize;
    private int _maxItems;
    private bool _initialized;
    private bool _disposed;
    private bool _overflowIsPinned;
    private bool _persistentHistoryEnabled;

    public HistoryService(
        IHistoryRepository repository,
        int maxItems = 32,
        int pageSize = 4,
        TimeProvider? timeProvider = null,
        Guid? activeWorkspaceId = null,
        PersistentHistoryService? persistentHistory = null,
        bool persistentHistoryEnabled = true)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _persistentHistory = persistentHistory;
        _activeWorkspaceId = activeWorkspaceId is { } value && value != Guid.Empty
            ? value
            : WorkspaceDefinition.DefaultWorkspaceId;
        _maxItems = Math.Clamp(maxItems, 8, 128);
        _pageSize = Math.Clamp(pageSize, 3, 6);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistentHistoryEnabled = persistentHistoryEnabled;
        _saveQueue = new CoalescingSaveQueue<WorkspaceHistorySave>(
            CreatePersistenceSnapshot,
            (snapshot, token) => _repository.SaveAsync(
                snapshot.WorkspaceId,
                snapshot.Entries,
                token),
            SaveDebounce);
    }

    public event EventHandler<HistoryChangedEventArgs>? Changed;

    public HistorySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return CreateSnapshotLocked();
            }
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ClipboardEntry> loaded = await _repository.LoadAsync(
                _activeWorkspaceId,
                cancellationToken)
            .ConfigureAwait(false);

        HistorySnapshot snapshot;
        bool normalized;
        var evictedEntries = new List<ClipboardEntry>();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            List<ClipboardEntry> clean = loaded
                .Where(entry => entry is not null)
                .GroupBy(entry => entry.Id)
                .Select(group => group.First())
                .OrderByDescending(entry => entry.CapturedAtUtc)
                .ToList();
            normalized = clean.Count != loaded.Count || !clean.SequenceEqual(loaded);
            _entries = clean;
            normalized |= EnforceLimitLocked(evictedEntries);
            _initialized = true;
            snapshot = CreateSnapshotLocked();
        }

        if (normalized)
        {
            _saveQueue.RequestSave();
        }

        if (_persistentHistory is not null && _persistentHistoryEnabled)
        {
            await _persistentHistory.SeedFromRichHistoryOnceAsync(
                    _activeWorkspaceId,
                    snapshot.Entries,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await DeletePersistentImagesAsync(
                _activeWorkspaceId,
                evictedEntries,
                cancellationToken)
            .ConfigureAwait(false);
        RaiseChanged(snapshot);
    }

    public async ValueTask<ClipboardEntry?> AddAsync(
        ClipboardPayload payload,
        bool allowDuplicate,
        CancellationToken cancellationToken = default,
        bool removeOldDuplicateEntries = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.IsEmpty)
        {
            return null;
        }

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry? result;
            HistorySnapshot snapshot;
            Guid workspaceId;
            bool persistentHistoryEnabled;
            ClipboardEntry[] duplicateEntries;
            var evictedEntries = new List<ClipboardEntry>();
            bool resultRetained;
            lock (_gate)
            {
                EnsureReadyLocked();
                if (!allowDuplicate &&
                    _entries.FirstOrDefault()?.Payload.ContentEquals(payload) == true)
                {
                    return null;
                }

                // Clipboard producers such as the Windows snipping tools can publish the same
                // image more than once while they finish populating formats.  With duplicate
                // cleanup enabled, replacing an entry that is already the newest one only
                // creates a remove/insert UI transition (and no meaningful history change).
                // Keep that newest entry in place; a matching entry farther down the history is
                // still replaced below, so an intentional later re-copy continues to move it up.
                if (removeOldDuplicateEntries &&
                    _entries.FirstOrDefault()?.Payload.ContentEquals(payload) == true)
                {
                    return null;
                }

                duplicateEntries = removeOldDuplicateEntries
                    ? _entries.Where(entry => entry.Payload.ContentEquals(payload)).ToArray()
                    : [];
                workspaceId = _activeWorkspaceId;
            }

            if (_persistentHistory is not null && duplicateEntries.Length > 0)
            {
                PersistentHistoryMatch[] matches = duplicateEntries
                    .Select(entry => new PersistentHistoryMatch(
                        entry.CapturedAtUtc,
                        PersistentHistoryCodec.Encode(entry.Payload)))
                    .ToArray();
                await _persistentHistory.DeleteMatchingRangeAsync(
                        workspaceId,
                        matches,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_gate)
            {
                EnsureReadyLocked();
                Guid? viewportAnchorId = GetViewportAnchorIdLocked();
                result = ClipboardEntry.Create(payload, _timeProvider.GetUtcNow());
                _entries.Insert(0, result);
                if (removeOldDuplicateEntries)
                {
                    _entries.RemoveAll(entry =>
                        entry.Id != result.Id && entry.Payload.ContentEquals(payload));
                }

                _ = EnforceLimitLocked(evictedEntries);
                resultRetained = _entries.Any(entry => entry.Id == result.Id);
                RestoreViewportAnchorLocked(viewportAnchorId);
                snapshot = CreateSnapshotLocked();
                workspaceId = _activeWorkspaceId;
                persistentHistoryEnabled = _persistentHistoryEnabled;
            }

            PersistAndRaise(snapshot);
            await DeletePersistentImagesAsync(
                    workspaceId,
                    evictedEntries,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_persistentHistory is not null &&
                persistentHistoryEnabled &&
                (!result.Payload.IsImage || resultRetained))
            {
                await _persistentHistory.AppendAsync(
                        workspaceId,
                        result.CapturedAtUtc,
                        result.Payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public ValueTask<ClipboardEntry> CreateBlankAsync(
        CancellationToken cancellationToken = default) =>
        CreateBlankAsync(BlankEntryDefaultText, cancellationToken);

    public async ValueTask<ClipboardEntry> CreateBlankAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ClipboardPayload payload = string.Equals(
            text,
            BlankEntryDefaultText,
            StringComparison.Ordinal)
            ? BlankEntryPayload
            : new ClipboardPayload(new Dictionary<ClipboardFormat, string>
            {
                [ClipboardFormat.UnicodeText] = text,
            });

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry result;
            HistorySnapshot snapshot;
            Guid workspaceId;
            bool persistentHistoryEnabled;
            var evictedEntries = new List<ClipboardEntry>();
            lock (_gate)
            {
                EnsureReadyLocked();
                result = ClipboardEntry.Create(payload, _timeProvider.GetUtcNow());
                _entries.Insert(0, result);
                _ = EnforceLimitLocked(evictedEntries);

                // A user-created draft should always be visible immediately, even if the
                // previous view was filtered or scrolled away from the newest entries.
                _externalFilter = null;
                _searchText = string.Empty;
                _startIndex = 0;
                snapshot = CreateSnapshotLocked();
                workspaceId = _activeWorkspaceId;
                persistentHistoryEnabled = _persistentHistoryEnabled;
            }

            PersistAndRaise(snapshot);
            await DeletePersistentImagesAsync(
                    workspaceId,
                    evictedEntries,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_persistentHistory is not null && persistentHistoryEnabled)
            {
                await _persistentHistory.AppendAsync(
                        workspaceId,
                        result.CapturedAtUtc,
                        result.Payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask<bool> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry removedEntry;
            Guid workspaceId;
            lock (_gate)
            {
                EnsureReadyLocked();
                int index = _entries.FindIndex(entry => entry.Id == id);
                if (index < 0)
                {
                    return false;
                }

                removedEntry = _entries[index];
                workspaceId = _activeWorkspaceId;
            }

            if (_persistentHistory is not null && !removedEntry.Payload.IsEmpty)
            {
                await _persistentHistory.DeleteMatchingAsync(
                        workspaceId,
                        removedEntry.CapturedAtUtc,
                        PersistentHistoryCodec.Encode(removedEntry.Payload),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            HistorySnapshot snapshot;
            lock (_gate)
            {
                EnsureReadyLocked();
                int index = _entries.FindIndex(entry => entry.Id == id);
                if (index < 0)
                {
                    return false;
                }

                _entries.RemoveAt(index);
                _overflowIsPinned = false;
                snapshot = CreateSnapshotLocked();
            }

            PersistAndRaise(snapshot);
            return true;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask<bool> RemovePersistentEntryAsync(
        Guid workspaceId,
        long storageKey,
        DateTimeOffset capturedAtUtc,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        if (storageKey <= 0 || _persistentHistory is null)
        {
            return false;
        }

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool removed = await _persistentHistory.DeleteAsync(
                    workspaceId,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!removed)
            {
                return false;
            }

            HistorySnapshot? snapshot = null;
            lock (_gate)
            {
                EnsureReadyLocked();
                if (_activeWorkspaceId == workspaceId)
                {
                    int index = _entries.FindIndex(entry =>
                        entry.CapturedAtUtc == capturedAtUtc.ToUniversalTime() &&
                        string.Equals(
                            PersistentHistoryCodec.Encode(entry.Payload),
                            text ?? string.Empty,
                            StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        _entries.RemoveAt(index);
                        _overflowIsPinned = false;
                        snapshot = CreateSnapshotLocked();
                    }
                }
            }

            if (snapshot is not null)
            {
                PersistAndRaise(snapshot);
            }

            return true;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask<bool> UpdateAsync(
        ClipboardEntry updatedEntry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(updatedEntry);

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry originalEntry;
            Guid workspaceId;
            bool persistentHistoryEnabled;
            lock (_gate)
            {
                EnsureReadyLocked();
                int index = _entries.FindIndex(entry => entry.Id == updatedEntry.Id);
                if (index < 0)
                {
                    return false;
                }

                if (_entries[index] == updatedEntry)
                {
                    return true;
                }

                originalEntry = _entries[index];
                workspaceId = _activeWorkspaceId;
                persistentHistoryEnabled = _persistentHistoryEnabled;
            }

            if (_persistentHistory is not null)
            {
                string originalText = PersistentHistoryCodec.Encode(originalEntry.Payload);
                string replacementText = PersistentHistoryCodec.Encode(updatedEntry.Payload);
                bool timestampChanged =
                    originalEntry.CapturedAtUtc != updatedEntry.CapturedAtUtc;
                bool textChanged = !string.Equals(
                    originalText,
                    replacementText,
                    StringComparison.Ordinal);
                if (timestampChanged)
                {
                    bool removedExisting = !originalEntry.Payload.IsEmpty &&
                        await _persistentHistory.DeleteMatchingAsync(
                                workspaceId,
                                originalEntry.CapturedAtUtc,
                                originalText,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!updatedEntry.Payload.IsEmpty &&
                        (removedExisting || persistentHistoryEnabled))
                    {
                        await _persistentHistory.AppendAsync(
                                workspaceId,
                                updatedEntry.CapturedAtUtc,
                                replacementText,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (textChanged)
                {
                    if (originalEntry.Payload.IsEmpty)
                    {
                        if (!updatedEntry.Payload.IsEmpty && persistentHistoryEnabled)
                        {
                            await _persistentHistory.AppendAsync(
                                    workspaceId,
                                    updatedEntry.CapturedAtUtc,
                                    replacementText,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else if (updatedEntry.Payload.IsEmpty)
                    {
                        await _persistentHistory.DeleteMatchingAsync(
                                workspaceId,
                                originalEntry.CapturedAtUtc,
                                originalText,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        bool replaced = await _persistentHistory.ReplaceMatchingTextAsync(
                                workspaceId,
                                originalEntry.CapturedAtUtc,
                                originalText,
                                replacementText,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!replaced && persistentHistoryEnabled)
                        {
                            await _persistentHistory.AppendAsync(
                                    workspaceId,
                                    updatedEntry.CapturedAtUtc,
                                    replacementText,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }

            HistorySnapshot snapshot;
            var evictedEntries = new List<ClipboardEntry>();
            lock (_gate)
            {
                EnsureReadyLocked();
                int index = _entries.FindIndex(entry => entry.Id == updatedEntry.Id);
                if (index < 0)
                {
                    return false;
                }

                _entries[index] = updatedEntry;
                _ = EnforceLimitLocked(evictedEntries);
                snapshot = CreateSnapshotLocked();
            }

            PersistAndRaise(snapshot);
            await DeletePersistentImagesAsync(
                    workspaceId,
                    evictedEntries,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry[] entries;
            Guid workspaceId;
            lock (_gate)
            {
                EnsureReadyLocked();
                entries = _entries.ToArray();
                workspaceId = _activeWorkspaceId;
            }

            if (_persistentHistory is not null)
            {
                PersistentHistoryMatch[] matches = entries
                    .Where(entry => !entry.Payload.IsEmpty)
                    .Select(entry => new PersistentHistoryMatch(
                        entry.CapturedAtUtc,
                        PersistentHistoryCodec.Encode(entry.Payload)))
                    .ToArray();
                await _persistentHistory.DeleteMatchingRangeAsync(
                        workspaceId,
                        matches,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            HistorySnapshot snapshot;
            lock (_gate)
            {
                EnsureReadyLocked();
                _entries.Clear();
                ResetViewLocked();
                snapshot = CreateSnapshotLocked();
            }

            PersistAndRaise(snapshot);
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask<int> RemoveImagesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardEntry[] images;
            Guid workspaceId;
            lock (_gate)
            {
                EnsureReadyLocked();
                images = _entries.Where(entry => entry.Payload.IsImage).ToArray();
                workspaceId = _activeWorkspaceId;
            }

            if (images.Length == 0)
            {
                return 0;
            }

            await DeletePersistentImagesAsync(workspaceId, images, cancellationToken)
                .ConfigureAwait(false);

            HistorySnapshot snapshot;
            lock (_gate)
            {
                EnsureReadyLocked();
                HashSet<Guid> imageIds = images.Select(entry => entry.Id).ToHashSet();
                _entries.RemoveAll(entry => imageIds.Contains(entry.Id));
                ResetViewLocked();
                snapshot = CreateSnapshotLocked();
            }

            PersistAndRaise(snapshot);
            return images.Length;
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask SwitchWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                if (_activeWorkspaceId == workspaceId)
                {
                    return;
                }
            }

            await _saveQueue.FlushAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ClipboardEntry> loaded = await _repository.LoadAsync(
                    workspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            (List<ClipboardEntry> clean, bool normalized) = NormalizeEntries(loaded);
            var evictedEntries = new List<ClipboardEntry>();
            bool persistentHistoryEnabled;
            lock (_gate)
            {
                persistentHistoryEnabled = _persistentHistoryEnabled;
            }

            HistorySnapshot snapshot;
            lock (_gate)
            {
                EnsureReadyLocked();
                _activeWorkspaceId = workspaceId;
                _entries = clean;
                normalized |= EnforceLimitLocked(evictedEntries);
                ResetViewLocked();
                snapshot = CreateSnapshotLocked();
            }

            if (_persistentHistory is not null && persistentHistoryEnabled)
            {
                await _persistentHistory.SeedFromRichHistoryOnceAsync(
                        workspaceId,
                        snapshot.Entries,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await DeletePersistentImagesAsync(
                    workspaceId,
                    evictedEntries,
                    cancellationToken)
                .ConfigureAwait(false);
            if (normalized)
            {
                _saveQueue.RequestSave();
            }

            RaiseChanged(snapshot);
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask DeleteWorkspaceAsync(
        Guid workspaceId,
        Guid replacementWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || replacementWorkspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace ids cannot be empty.");
        }

        if (workspaceId == replacementWorkspaceId)
        {
            throw new ArgumentException("A deleted workspace cannot replace itself.");
        }

        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool deletingActive;
            lock (_gate)
            {
                EnsureReadyLocked();
                deletingActive = _activeWorkspaceId == workspaceId;
            }

            await _saveQueue.FlushAsync(cancellationToken).ConfigureAwait(false);

            List<ClipboardEntry>? replacementEntries = null;
            bool normalized = false;
            var evictedEntries = new List<ClipboardEntry>();
            if (deletingActive)
            {
                IReadOnlyList<ClipboardEntry> loaded = await _repository.LoadAsync(
                        replacementWorkspaceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                (replacementEntries, normalized) = NormalizeEntries(loaded);
            }

            await _repository.DeleteAsync(workspaceId, cancellationToken).ConfigureAwait(false);

            if (deletingActive)
            {
                HistorySnapshot snapshot;
                lock (_gate)
                {
                    EnsureReadyLocked();
                    _activeWorkspaceId = replacementWorkspaceId;
                    _entries = replacementEntries!;
                    normalized |= EnforceLimitLocked(evictedEntries);
                    ResetViewLocked();
                    snapshot = CreateSnapshotLocked();
                }

                if (normalized)
                {
                    _saveQueue.RequestSave();
                }

                RaiseChanged(snapshot);
                await DeletePersistentImagesAsync(
                        replacementWorkspaceId,
                        evictedEntries,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public void SetSearch(string? searchText, bool enabled)
    {
        HistorySnapshot snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            string normalizedSearchText = enabled
                ? searchText?.Trim() ?? string.Empty
                : string.Empty;
            if (_externalFilter is null &&
                string.IsNullOrEmpty(_searchText) &&
                string.IsNullOrEmpty(normalizedSearchText))
            {
                // Closing an unused search box must not reset a scrolled viewport.
                return;
            }

            _externalFilter = null;
            _searchText = normalizedSearchText;
            _startIndex = 0;
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
    }

    public void SetExternalFilter(IEnumerable<Guid> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        HistorySnapshot snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            _searchText = string.Empty;
            _externalFilter = entryIds.Where(id => id != Guid.Empty).ToHashSet();
            _startIndex = 0;
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
    }

    public void ClearFilter() => SetSearch(string.Empty, enabled: false);

    public void MoveUp() => MoveBy(-1);

    public void MoveDown() => MoveBy(1);

    public void MoveTop()
    {
        HistorySnapshot snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            _startIndex = 0;
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
    }

    public void SetPageSize(int pageSize)
    {
        HistorySnapshot snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            _pageSize = Math.Clamp(pageSize, 3, 6);
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
    }

    public void SetMaxItems(int maxItems)
    {
        _workspaceGate.Wait();
        try
        {
            HistorySnapshot snapshot;
            bool changed;
            Guid workspaceId;
            var evictedEntries = new List<ClipboardEntry>();
            lock (_gate)
            {
                EnsureReadyLocked();
                int normalized = Math.Clamp(maxItems, 8, 128);
                changed = _maxItems != normalized;
                _maxItems = normalized;
                changed |= EnforceLimitLocked(evictedEntries);
                snapshot = CreateSnapshotLocked();
                workspaceId = _activeWorkspaceId;
            }

            DeletePersistentImagesAsync(workspaceId, evictedEntries, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (changed)
            {
                PersistAndRaise(snapshot);
            }
            else
            {
                RaiseChanged(snapshot);
            }
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public void SetPersistentHistoryEnabled(bool enabled)
    {
        lock (_gate)
        {
            EnsureReadyLocked();
            _persistentHistoryEnabled = enabled;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _saveQueue.FlushAsync(cancellationToken);

    /// <summary>
    /// Replaces resident bodies with repository-backed payloads after a save has settled. The
    /// collection and viewport identity stay intact; only non-visible content becomes lazy again.
    /// This is called by the presentation shell only while the dispatcher is idle.
    /// </summary>
    public async ValueTask ReleasePersistedBodiesAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _saveQueue.FlushAsync(cancellationToken).ConfigureAwait(false);
            Guid workspaceId;
            lock (_gate)
            {
                EnsureReadyLocked();
                if (_entries.All(entry => entry.Payload.IsDeferred))
                {
                    return;
                }

                workspaceId = _activeWorkspaceId;
            }

            IReadOnlyList<ClipboardEntry> loaded = await _repository.LoadAsync(
                    workspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            (List<ClipboardEntry> entries, _) = NormalizeEntries(loaded);
            HistorySnapshot snapshot;
            lock (_gate)
            {
                EnsureReadyLocked();
                if (_activeWorkspaceId != workspaceId)
                {
                    return;
                }

                _entries = entries;
                snapshot = CreateSnapshotLocked();
            }

            RaiseChanged(snapshot);
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _workspaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            await _saveQueue.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _disposed = true;
            }
        }
        finally
        {
            _workspaceGate.Release();
        }
    }

    private void MoveBy(int delta)
    {
        HistorySnapshot snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int filteredCount = CreateFilteredEntriesLocked().Count;
            int maxStart = Math.Max(0, filteredCount - _pageSize);
            _startIndex = Math.Clamp(_startIndex + delta, 0, maxStart);
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
    }

    private bool EnforceLimitLocked(List<ClipboardEntry>? removedEntries = null)
    {
        bool changed = false;
        while (_entries.Count > _maxItems)
        {
            int removableIndex = _entries.FindLastIndex(entry => !entry.IsPinned);
            if (removableIndex < 0)
            {
                _overflowIsPinned = true;
                return changed;
            }

            ClipboardEntry removed = _entries[removableIndex];
            _entries.RemoveAt(removableIndex);
            removedEntries?.Add(removed);
            changed = true;
        }

        _overflowIsPinned = false;
        return changed;
    }

    private async ValueTask DeletePersistentImagesAsync(
        Guid workspaceId,
        IEnumerable<ClipboardEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_persistentHistory is null)
        {
            return;
        }

        foreach (ClipboardEntry entry in entries.Where(entry => entry.Payload.IsImage))
        {
            _ = await _persistentHistory.DeleteMatchingAsync(
                    workspaceId,
                    entry.CapturedAtUtc,
                    PersistentHistoryCodec.Encode(entry.Payload),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private HistorySnapshot CreateSnapshotLocked()
    {
        ClipboardEntry[] all = _entries.ToArray();
        List<ClipboardEntry> filtered = CreateFilteredEntriesLocked();
        int maxStart = Math.Max(0, filtered.Count - _pageSize);
        _startIndex = Math.Clamp(_startIndex, 0, maxStart);
        ClipboardEntry[] visible = filtered.Skip(_startIndex).Take(_pageSize).ToArray();
        bool isFiltered = _externalFilter is not null || !string.IsNullOrEmpty(_searchText);

        return new HistorySnapshot(
            all,
            filtered.ToArray(),
            visible,
            _startIndex,
            _pageSize,
            isFiltered,
            _startIndex == 0,
            _startIndex >= maxStart,
            _overflowIsPinned);
    }

    private List<ClipboardEntry> CreateFilteredEntriesLocked()
    {
        if (IsFilteredEntriesCacheValidLocked())
        {
            return _filteredEntriesCache!;
        }

        IEnumerable<ClipboardEntry> selected = _entries;
        if (_externalFilter is not null)
        {
            selected = selected.Where(entry => _externalFilter.Contains(entry.Id));
        }
        else if (!string.IsNullOrEmpty(_searchText))
        {
            bool includeImages = ImageSearchTerms.IncludesImages(_searchText);
            selected = selected.Where(entry =>
                (includeImages && entry.Payload.IsImage) ||
                (!entry.Payload.IsImage &&
                 entry.Payload.PrimaryText.Contains(
                     _searchText,
                     StringComparison.OrdinalIgnoreCase)));
        }

        List<ClipboardEntry> filtered;
        if (_externalFilter is not null || !string.IsNullOrEmpty(_searchText))
        {
            // Legacy search/AI-filter behavior preserves the original history order.
            filtered = selected.ToList();
        }
        else
        {
            filtered = selected
                .OrderByDescending(entry => entry.IsPinned)
                .ThenBy(entry => _entries.IndexOf(entry))
                .ToList();
        }

        _filteredEntriesCache = filtered;
        _filteredEntriesCacheSource = _entries.ToArray();
        _filteredEntriesCacheExternalFilter = _externalFilter;
        _filteredEntriesCacheSearchText = _searchText;
        return filtered;
    }

    private bool IsFilteredEntriesCacheValidLocked()
    {
        if (_filteredEntriesCache is null ||
            _filteredEntriesCacheSource is null ||
            !ReferenceEquals(_externalFilter, _filteredEntriesCacheExternalFilter) ||
            !string.Equals(_searchText, _filteredEntriesCacheSearchText, StringComparison.Ordinal) ||
            _entries.Count != _filteredEntriesCacheSource.Length)
        {
            return false;
        }

        for (int index = 0; index < _entries.Count; index++)
        {
            if (!ReferenceEquals(_entries[index], _filteredEntriesCacheSource[index]))
            {
                return false;
            }
        }

        return true;
    }

    private Guid? GetViewportAnchorIdLocked()
    {
        if (_startIndex <= 0)
        {
            return null;
        }

        List<ClipboardEntry> filtered = CreateFilteredEntriesLocked();
        return _startIndex < filtered.Count
            ? filtered[_startIndex].Id
            : null;
    }

    private void RestoreViewportAnchorLocked(Guid? viewportAnchorId)
    {
        if (viewportAnchorId is null)
        {
            _startIndex = 0;
            return;
        }

        List<ClipboardEntry> filtered = CreateFilteredEntriesLocked();
        int anchorIndex = filtered.FindIndex(entry => entry.Id == viewportAnchorId.Value);
        if (anchorIndex >= 0)
        {
            _startIndex = anchorIndex;
        }
    }

    private WorkspaceHistorySave CreatePersistenceSnapshot()
    {
        lock (_gate)
        {
            return new WorkspaceHistorySave(_activeWorkspaceId, _entries.ToArray());
        }
    }

    private static (List<ClipboardEntry> Entries, bool Normalized) NormalizeEntries(
        IReadOnlyList<ClipboardEntry> loaded)
    {
        List<ClipboardEntry> clean = loaded
            .Where(entry => entry is not null)
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.CapturedAtUtc)
            .ToList();
        bool normalized = clean.Count != loaded.Count || !clean.SequenceEqual(loaded);
        return (clean, normalized);
    }

    private void ResetViewLocked()
    {
        _externalFilter = null;
        _searchText = string.Empty;
        _startIndex = 0;
        _overflowIsPinned = false;
    }

    private void PersistAndRaise(HistorySnapshot snapshot)
    {
        _saveQueue.RequestSave();
        RaiseChanged(snapshot);
    }

    private void RaiseChanged(HistorySnapshot snapshot) =>
        Changed?.Invoke(this, new HistoryChangedEventArgs(snapshot));

    private void EnsureReadyLocked()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("History service has not been initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record WorkspaceHistorySave(
        Guid WorkspaceId,
        IReadOnlyList<ClipboardEntry> Entries);
}
