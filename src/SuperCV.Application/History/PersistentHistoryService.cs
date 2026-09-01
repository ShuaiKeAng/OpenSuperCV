using SuperCV.Application.Ports;
using SuperCV.Domain.History;

namespace SuperCV.Application.History;

public sealed class PersistentHistoryService : IAsyncDisposable
{
    public const int MaximumItemsPerWorkspace = 99_999;
    public const int MaximumPageSize = 500;

    private readonly IPersistentHistoryRepository _repository;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public PersistentHistoryService(IPersistentHistoryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public event EventHandler<PersistentHistoryChangedEventArgs>? Changed;

    public ValueTask AppendAsync(
        Guid workspaceId,
        DateTimeOffset capturedAtUtc,
        string text,
        CancellationToken cancellationToken = default) =>
        AppendRangeAsync(
            workspaceId,
            [new PersistentHistoryWrite(capturedAtUtc, text ?? string.Empty)],
            cancellationToken);

    public ValueTask AppendAsync(
        Guid workspaceId,
        DateTimeOffset capturedAtUtc,
        SuperCV.Domain.Clipboard.ClipboardPayload payload,
        CancellationToken cancellationToken = default) =>
        AppendRangeAsync(
            workspaceId,
            [new PersistentHistoryWrite(capturedAtUtc, payload)],
            cancellationToken);

    public async ValueTask AppendRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        PersistentHistoryWrite[] snapshot = entries
            .Select(entry => entry ?? throw new ArgumentException(
                "Persistent history entries cannot contain null values.",
                nameof(entries)))
            .Select(entry => entry.Snapshot())
            .ToArray();

        await ExecuteOnWorkerAsync(
                token => _repository.AppendRangeAsync(workspaceId, snapshot, token),
                cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
    }

    public async ValueTask SeedFromRichHistoryOnceAsync(
        Guid workspaceId,
        IReadOnlyList<ClipboardEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(entries);

        PersistentHistoryWrite[] snapshot = entries
            .Where(entry => entry is not null && !entry.Payload.IsEmpty)
            .OrderBy(entry => entry.CapturedAtUtc)
            .Select(entry => new PersistentHistoryWrite(entry.CapturedAtUtc, entry.Payload))
            .ToArray();
        await ExecuteOnWorkerAsync(
                token => _repository.SeedOnceAsync(workspaceId, snapshot, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<PersistentHistoryPage> QueryAsync(
        Guid workspaceId,
        string? searchText,
        int limit,
        PersistentHistoryCursor? cursor = null,
        PersistentHistoryDateRange? dateRange = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        dateRange?.Validate();
        int normalizedLimit = Math.Clamp(limit, 1, MaximumPageSize);
        string normalizedSearch = searchText?.Trim() ?? string.Empty;
        return ExecuteOnWorkerAsync(
            token => _repository.QueryAsync(
                workspaceId,
                normalizedSearch,
                normalizedLimit,
                cursor,
                dateRange,
                token),
            cancellationToken);
    }

    public async ValueTask<bool> DeleteAsync(
        Guid workspaceId,
        long storageKey,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        if (storageKey <= 0)
        {
            return false;
        }

        bool removed = await ExecuteOnWorkerAsync(
                token => _repository.DeleteAsync(workspaceId, storageKey, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (removed)
        {
            Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
        }

        return removed;
    }

    public async ValueTask<bool> DeleteMatchingAsync(
        Guid workspaceId,
        DateTimeOffset capturedAtUtc,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        var match = new PersistentHistoryMatch(
            capturedAtUtc.ToUniversalTime(),
            text ?? string.Empty);
        bool removed = await ExecuteOnWorkerAsync(
                token => _repository.DeleteMatchingAsync(workspaceId, match, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (removed)
        {
            Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
        }

        return removed;
    }

    public async ValueTask<int> DeleteMatchingRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryMatch> matches,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return 0;
        }

        PersistentHistoryMatch[] snapshot = matches
            .Select(match => match ?? throw new ArgumentException(
                "Persistent history matches cannot contain null values.",
                nameof(matches)))
            .Select(match => new PersistentHistoryMatch(
                match.CapturedAtUtc.ToUniversalTime(),
                match.Text ?? string.Empty))
            .ToArray();
        int removedCount = await ExecuteOnWorkerAsync(
                token => _repository.DeleteMatchingRangeAsync(workspaceId, snapshot, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (removedCount > 0)
        {
            Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
        }

        return removedCount;
    }

        public async ValueTask<bool> ReplaceMatchingTextAsync(
            Guid workspaceId,
            DateTimeOffset capturedAtUtc,
            string originalText,
            string replacementText,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        var match = new PersistentHistoryMatch(
            capturedAtUtc.ToUniversalTime(),
            originalText ?? string.Empty);
        bool replaced = await ExecuteOnWorkerAsync(
                token => _repository.ReplaceMatchingTextAsync(
                    workspaceId,
                    match,
                    replacementText ?? string.Empty,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        if (replaced)
        {
            Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
        }

            return replaced;
        }

        public async ValueTask<int> TrimToLimitAsync(
            Guid workspaceId,
            int retainedItems,
            CancellationToken cancellationToken = default)
        {
            ValidateWorkspaceId(workspaceId);
            int normalizedRetainedItems = Math.Clamp(
                retainedItems,
                0,
                MaximumItemsPerWorkspace);
            int removedCount = await ExecuteOnWorkerAsync(
                    token => _repository.TrimToLimitAsync(
                        workspaceId,
                        normalizedRetainedItems,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (removedCount > 0)
            {
                Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
            }

            return removedCount;
        }

        public async ValueTask<int> DeleteImagesAsync(
            Guid workspaceId,
            CancellationToken cancellationToken = default)
        {
            ValidateWorkspaceId(workspaceId);
            int removedCount = await ExecuteOnWorkerAsync(
                    token => _repository.DeleteImagesAsync(workspaceId, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (removedCount > 0)
            {
                Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
            }

            return removedCount;
        }

        public async ValueTask DeleteWorkspaceAsync(
            Guid workspaceId,
            CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        await ExecuteOnWorkerAsync(
                token => _repository.DeleteWorkspaceAsync(workspaceId, token),
                cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, new PersistentHistoryChangedEventArgs(workspaceId));
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _repository.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async ValueTask ExecuteOnWorkerAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Task.Run(
                    async () => await operation(cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<T> ExecuteOnWorkerAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                    async () => await operation(cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static void ValidateWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
