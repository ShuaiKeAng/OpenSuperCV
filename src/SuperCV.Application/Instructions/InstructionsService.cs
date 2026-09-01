using SuperCV.Application.Ports;
using SuperCV.Domain.Instructions;

namespace SuperCV.Application.Instructions;

public sealed class InstructionsChangedEventArgs : EventArgs
{
    public InstructionsChangedEventArgs(IReadOnlyList<CustomInstruction> instructions)
    {
        Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
    }

    public IReadOnlyList<CustomInstruction> Instructions { get; }
}

public sealed class InstructionsService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IInstructionRepository _repository;
    private readonly CoalescingSaveQueue<IReadOnlyList<CustomInstruction>> _saveQueue;
    private List<CustomInstruction> _instructions = [];
    private bool _initialized;
    private bool _disposed;

    public InstructionsService(IInstructionRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _saveQueue = new CoalescingSaveQueue<IReadOnlyList<CustomInstruction>>(
            CreatePersistenceSnapshot,
            (snapshot, token) => _repository.SaveAsync(snapshot, token),
            TimeSpan.FromMilliseconds(200));
    }

    public event EventHandler<InstructionsChangedEventArgs>? Changed;

    public IReadOnlyList<CustomInstruction> Snapshot
    {
        get
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                return _instructions.ToArray();
            }
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CustomInstruction> loaded = await _repository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        CustomInstruction[] snapshot;
        bool normalized;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            _instructions = loaded
                .Where(instruction => instruction is not null)
                .GroupBy(instruction => instruction.Id)
                .Select(group => group.First())
                .OrderBy(instruction => instruction.CreatedAtUtc)
                .ThenBy(instruction => instruction.Id)
                .ToList();
            normalized = _instructions.Count != loaded.Count || !_instructions.SequenceEqual(loaded);
            _initialized = true;
            snapshot = _instructions.ToArray();
        }

        if (normalized)
        {
            _saveQueue.RequestSave();
        }

        RaiseChanged(snapshot);
    }

    public ValueTask<bool> AddAsync(
        CustomInstruction instruction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instruction);

        CustomInstruction[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            if (_instructions.Any(item => item.Id == instruction.Id))
            {
                return ValueTask.FromResult(false);
            }

            _instructions.Add(instruction);
            SortLocked();
            snapshot = _instructions.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> UpdateAsync(
        CustomInstruction instruction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instruction);

        CustomInstruction[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _instructions.FindIndex(item => item.Id == instruction.Id);
            if (index < 0)
            {
                return ValueTask.FromResult(false);
            }

            _instructions[index] = instruction;
            SortLocked();
            snapshot = _instructions.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CustomInstruction[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _instructions.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return ValueTask.FromResult(false);
            }

            _instructions.RemoveAt(index);
            snapshot = _instructions.ToArray();
        }

        PersistAndRaise(snapshot);
        return ValueTask.FromResult(true);
    }

    public string? GetDocumentPath(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An instruction id cannot be empty.", nameof(id));
        }

        lock (_gate)
        {
            EnsureReadyLocked();
            if (_instructions.All(instruction => instruction.Id != id))
            {
                return null;
            }
        }

        return _repository.GetDocumentPath(id);
    }

    public async ValueTask<bool> RefreshDocumentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An instruction id cannot be empty.", nameof(id));
        }

        CustomInstruction expected;
        lock (_gate)
        {
            EnsureReadyLocked();
            expected = _instructions.FirstOrDefault(instruction => instruction.Id == id)
                ?? throw new KeyNotFoundException($"Instruction '{id:D}' was not found.");
        }

        CustomInstruction? repaired = await _repository
            .RepairDocumentAsync(expected, cancellationToken)
            .ConfigureAwait(false);
        if (repaired is null)
        {
            return false;
        }

        CustomInstruction[] snapshot;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _instructions.FindIndex(instruction => instruction.Id == id);
            if (index < 0 || _instructions[index] == repaired)
            {
                return false;
            }

            _instructions[index] = repaired;
            snapshot = _instructions.ToArray();
        }

        RaiseChanged(snapshot);
        return true;
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

    private IReadOnlyList<CustomInstruction> CreatePersistenceSnapshot()
    {
        lock (_gate)
        {
            return _instructions.ToArray();
        }
    }

    private void PersistAndRaise(IReadOnlyList<CustomInstruction> snapshot)
    {
        _saveQueue.RequestSave();
        RaiseChanged(snapshot);
    }

    private void RaiseChanged(IReadOnlyList<CustomInstruction> snapshot) =>
        Changed?.Invoke(this, new InstructionsChangedEventArgs(snapshot));

    private void SortLocked() =>
        _instructions = _instructions
            .OrderBy(instruction => instruction.CreatedAtUtc)
            .ThenBy(instruction => instruction.Id)
            .ToList();

    private void EnsureReadyLocked()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Instructions service has not been initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
