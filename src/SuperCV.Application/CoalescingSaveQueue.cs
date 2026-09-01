namespace SuperCV.Application;

internal sealed class CoalescingSaveQueue<TSnapshot> : IAsyncDisposable
{
    private const int DisposeFlushAttempts = 3;
    private readonly object _gate = new();
    private readonly Func<TSnapshot> _createSnapshot;
    private readonly Func<TSnapshot, CancellationToken, ValueTask> _saveAsync;
    private readonly TimeSpan _debounceDelay;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FlushWaiter> _flushWaiters = new();
    private readonly Task _worker;
    private long _requestedVersion;
    private long _savedVersion;
    private bool _flushRequested;
    private bool _stopping;
    private bool _disposed;
    private Task? _disposeTask;

    internal CoalescingSaveQueue(
        Func<TSnapshot> createSnapshot,
        Func<TSnapshot, CancellationToken, ValueTask> saveAsync,
        TimeSpan debounceDelay)
    {
        ArgumentNullException.ThrowIfNull(createSnapshot);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounceDelay, TimeSpan.Zero);

        _createSnapshot = createSnapshot;
        _saveAsync = saveAsync;
        _debounceDelay = debounceDelay;
        _worker = Task.Run(RunAsync);
    }

    internal void RequestSave()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
            _requestedVersion++;
        }

        Signal();
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return (_disposeTask ?? Task.CompletedTask).WaitAsync(cancellationToken);
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_savedVersion >= _requestedVersion)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add(new FlushWaiter(_requestedVersion, completion));
            _flushRequested = true;
            Signal();
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _stopping = true;
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await FlushForDisposeWithRetryAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
            }

            _lifetime.Cancel();
            Signal();
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }

            _lifetime.Dispose();
            _signal.Dispose();
        }
    }

    private async Task FlushForDisposeWithRetryAsync()
    {
        for (int attempt = 1; attempt <= DisposeFlushAttempts; attempt++)
        {
            try
            {
                await FlushForDisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attempt < DisposeFlushAttempts)
            {
                // The worker keeps the dirty version pending and schedules its normal retry.
                // Re-register a disposal waiter so a single transient I/O failure cannot drop
                // the final snapshot during shutdown.
            }
        }

        throw new InvalidOperationException("The pending snapshot could not be persisted during shutdown.");
    }

    private Task FlushForDisposeAsync()
    {
        lock (_gate)
        {
            if (_savedVersion >= _requestedVersion)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add(new FlushWaiter(_requestedVersion, completion));
            _flushRequested = true;
            Signal();
            return completion.Task;
        }
    }

    private async Task RunAsync()
    {
        CancellationToken cancellationToken = _lifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            bool skipDelay;
            lock (_gate)
            {
                skipDelay = _flushRequested;
                _flushRequested = false;
            }

            if (!skipDelay && _debounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            }

            long version;
            lock (_gate)
            {
                version = _requestedVersion;
                if (_savedVersion >= version)
                {
                    CompleteSatisfiedWaiters();
                    continue;
                }
            }

            TSnapshot snapshot = _createSnapshot();
            try
            {
                await _saveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _savedVersion = Math.Max(_savedVersion, version);
                    CompleteSatisfiedWaiters();
                    if (_requestedVersion > _savedVersion)
                    {
                        Signal();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    FailWaitersUpTo(version, exception);
                }

                // Keep the dirty version pending. A transient disk failure is retried even if
                // no further UI mutation occurs; explicit Flush still receives the first error.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                Signal();
            }
        }
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CompleteSatisfiedWaiters()
    {
        for (int index = _flushWaiters.Count - 1; index >= 0; index--)
        {
            FlushWaiter waiter = _flushWaiters[index];
            if (waiter.Version <= _savedVersion)
            {
                _flushWaiters.RemoveAt(index);
                waiter.Completion.TrySetResult();
            }
        }
    }

    private void FailWaitersUpTo(long version, Exception exception)
    {
        for (int index = _flushWaiters.Count - 1; index >= 0; index--)
        {
            FlushWaiter waiter = _flushWaiters[index];
            if (waiter.Version <= version)
            {
                _flushWaiters.RemoveAt(index);
                waiter.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record FlushWaiter(long Version, TaskCompletionSource Completion);
}
