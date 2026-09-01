using SuperCV.Application.Ports;
using SuperCV.Domain.Bookmarks;
using SuperCV.Domain.Clipboard;

namespace SuperCV.Application.Bookmarks;

public sealed class BookmarksChangedEventArgs : EventArgs
{
    public BookmarksChangedEventArgs(IReadOnlyList<Bookmark> bookmarks)
    {
        Bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));
    }

    public IReadOnlyList<Bookmark> Bookmarks { get; }
}

public sealed class BookmarksService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IBookmarkRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly CoalescingSaveQueue<IReadOnlyList<Bookmark>> _saveQueue;
    private List<Bookmark> _bookmarks = new();
    private bool _initialized;
    private bool _disposed;

    public BookmarksService(IBookmarkRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _saveQueue = new CoalescingSaveQueue<IReadOnlyList<Bookmark>>(
            CreatePersistenceSnapshot,
            (snapshot, token) => _repository.SaveAsync(snapshot, token),
            TimeSpan.FromMilliseconds(200));
    }

    public event EventHandler<BookmarksChangedEventArgs>? Changed;

    public IReadOnlyList<Bookmark> Snapshot
    {
        get
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                return _bookmarks.ToArray();
            }
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Bookmark> loaded = await _repository.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        Bookmark[] snapshot;
        bool normalized;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            _bookmarks = loaded
                .Where(bookmark => bookmark is not null)
                .GroupBy(bookmark => bookmark.Id)
                .Select(group => group.First())
                .ToList();
            normalized = _bookmarks.Count != loaded.Count || !_bookmarks.SequenceEqual(loaded);
            _initialized = true;
            snapshot = _bookmarks.ToArray();
        }

        if (normalized)
        {
            _saveQueue.RequestSave();
        }

        RaiseChanged(snapshot);
    }

    public ValueTask<Bookmark?> AddAsync(
        string title,
        ClipboardPayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.IsEmpty)
        {
            return ValueTask.FromResult<Bookmark?>(null);
        }

        Bookmark bookmark;
        Bookmark[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            bookmark = new Bookmark(Guid.NewGuid(), title, payload, _timeProvider.GetUtcNow());
            _bookmarks.Add(bookmark);
            snapshot = _bookmarks.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult<Bookmark?>(bookmark);
    }

    public ValueTask<bool> UpdateAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bookmark);
        Bookmark[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _bookmarks.FindIndex(item => item.Id == bookmark.Id);
            if (index < 0)
            {
                return ValueTask.FromResult(false);
            }

            _bookmarks[index] = bookmark;
            snapshot = _bookmarks.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bookmark[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _bookmarks.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return ValueTask.FromResult(false);
            }

            _bookmarks.RemoveAt(index);
            snapshot = _bookmarks.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> MoveAsync(
        Guid id,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bookmark[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            if ((uint)targetIndex >= (uint)_bookmarks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(targetIndex));
            }

            int currentIndex = _bookmarks.FindIndex(item => item.Id == id);
            if (currentIndex < 0 || currentIndex == targetIndex)
            {
                return ValueTask.FromResult(false);
            }

            Bookmark bookmark = _bookmarks[currentIndex];
            _bookmarks.RemoveAt(currentIndex);
            _bookmarks.Insert(targetIndex, bookmark);
            snapshot = _bookmarks.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _saveQueue.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
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

    private IReadOnlyList<Bookmark> CreatePersistenceSnapshot()
    {
        lock (_gate)
        {
            return _bookmarks.ToArray();
        }
    }

    private void PersistAndRaise(IReadOnlyList<Bookmark> snapshot)
    {
        _saveQueue.RequestSave();
        RaiseChanged(snapshot);
    }

    private void RaiseChanged(IReadOnlyList<Bookmark> snapshot) =>
        Changed?.Invoke(this, new BookmarksChangedEventArgs(snapshot));

    private void EnsureReadyLocked()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Bookmarks service has not been initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
