using SuperCV.Application.Ports;
using SuperCV.Domain.Workspaces;

namespace SuperCV.Application.Workspaces;

public sealed class WorkspaceService : IAsyncDisposable
{
    public const int MaximumNameLength = 30;
    public const string DefaultWorkspaceName = "默认工作区";

    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private readonly IWorkspaceRepository _repository;
    private readonly CoalescingSaveQueue<WorkspaceState> _saveQueue;
    private List<WorkspaceDefinition> _workspaces = [];
    private Guid _activeWorkspaceId;
    private bool _initialized;
    private bool _disposed;

    public WorkspaceService(IWorkspaceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _saveQueue = new CoalescingSaveQueue<WorkspaceState>(
            CreatePersistenceSnapshot,
            (snapshot, token) => _repository.SaveAsync(snapshot, token),
            SaveDebounce);
    }

    public event EventHandler<WorkspacesChangedEventArgs>? Changed;

    public WorkspaceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                return CreateSnapshotLocked();
            }
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        WorkspaceState? loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool needsSave;
        WorkspaceSnapshot snapshot;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            List<WorkspaceDefinition> normalized = NormalizeWorkspaces(loaded?.Workspaces);
            Guid activeId = loaded is not null &&
                            normalized.Any(workspace => workspace.Id == loaded.ActiveWorkspaceId)
                ? loaded.ActiveWorkspaceId
                : normalized[0].Id;

            _workspaces = normalized;
            _activeWorkspaceId = activeId;
            _initialized = true;
            needsSave = loaded is null ||
                        loaded.ActiveWorkspaceId != activeId ||
                        !normalized.SequenceEqual(loaded.Workspaces);
            snapshot = CreateSnapshotLocked();
        }

        if (needsSave)
        {
            _saveQueue.RequestSave();
        }

        RaiseChanged(snapshot);
    }

    public WorkspaceDefinition Create(string name)
    {
        WorkspaceSnapshot snapshot;
        WorkspaceDefinition created;
        lock (_gate)
        {
            EnsureReadyLocked();
            string normalizedName = NormalizeName(name);
            EnsureUniqueNameLocked(normalizedName);

            created = WorkspaceDefinition.Create(normalizedName);
            _workspaces.Add(created);
            _activeWorkspaceId = created.Id;
            snapshot = CreateSnapshotLocked();
        }

        PersistAndRaise(snapshot);
        return created;
    }

    public void Select(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        WorkspaceSnapshot? snapshot = null;
        lock (_gate)
        {
            EnsureReadyLocked();
            EnsureWorkspaceExistsLocked(workspaceId);
            if (_activeWorkspaceId == workspaceId)
            {
                return;
            }

            _activeWorkspaceId = workspaceId;
            snapshot = CreateSnapshotLocked();
        }

        PersistAndRaise(snapshot);
    }

    public WorkspaceDefinition Rename(Guid workspaceId, string name)
    {
        WorkspaceSnapshot snapshot;
        WorkspaceDefinition updated;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _workspaces.FindIndex(workspace => workspace.Id == workspaceId);
            if (index < 0)
            {
                throw new InvalidOperationException("要重命名的工作区不存在。");
            }

            string normalizedName = NormalizeName(name);
            EnsureUniqueNameLocked(normalizedName, workspaceId);
            WorkspaceDefinition current = _workspaces[index];
            if (string.Equals(current.Name, normalizedName, StringComparison.Ordinal))
            {
                return current;
            }

            updated = current with { Name = normalizedName };
            _workspaces[index] = updated;
            snapshot = CreateSnapshotLocked();
        }

        PersistAndRaise(snapshot);
        return updated;
    }

    public WorkspaceDeleteResult Delete(Guid workspaceId)
    {
        WorkspaceSnapshot snapshot;
        WorkspaceDeleteResult result;
        lock (_gate)
        {
            EnsureReadyLocked();
            int index = _workspaces.FindIndex(workspace => workspace.Id == workspaceId);
            if (index < 0)
            {
                throw new InvalidOperationException("要删除的工作区不存在。");
            }

            if (_workspaces.Count == 1)
            {
                throw new InvalidOperationException("至少需要保留一个工作区。");
            }

            WorkspaceDefinition removed = _workspaces[index];
            _workspaces.RemoveAt(index);
            if (_activeWorkspaceId == workspaceId)
            {
                int replacementIndex = Math.Min(index, _workspaces.Count - 1);
                _activeWorkspaceId = _workspaces[replacementIndex].Id;
            }

            result = new WorkspaceDeleteResult(removed, _activeWorkspaceId);
            snapshot = CreateSnapshotLocked();
        }

        PersistAndRaise(snapshot);
        return result;
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

    public static string NormalizeName(string? name)
    {
        string normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("工作区名称不能为空。", nameof(name));
        }

        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException($"工作区名称不能超过 {MaximumNameLength} 个字符。", nameof(name));
        }

        return normalized;
    }

    private static List<WorkspaceDefinition> NormalizeWorkspaces(
        IReadOnlyList<WorkspaceDefinition>? workspaces)
    {
        var normalized = new List<WorkspaceDefinition>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (workspaces is not null)
        {
            foreach (WorkspaceDefinition? workspace in workspaces)
            {
                if (workspace is null || workspace.Id == Guid.Empty)
                {
                    continue;
                }

                string name;
                try
                {
                    name = NormalizeName(workspace.Name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (ids.Add(workspace.Id) && names.Add(name))
                {
                    normalized.Add(workspace with { Name = name });
                }
            }
        }

        if (normalized.Count == 0)
        {
            normalized.Add(new WorkspaceDefinition(
                WorkspaceDefinition.DefaultWorkspaceId,
                DefaultWorkspaceName));
        }

        return normalized;
    }

    private void EnsureUniqueNameLocked(string name, Guid? exceptWorkspaceId = null)
    {
        if (_workspaces.Any(workspace =>
                workspace.Id != exceptWorkspaceId &&
                string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("已存在同名工作区。");
        }
    }

    private void EnsureWorkspaceExistsLocked(Guid workspaceId)
    {
        if (!_workspaces.Any(workspace => workspace.Id == workspaceId))
        {
            throw new InvalidOperationException("所选工作区不存在。");
        }
    }

    private WorkspaceSnapshot CreateSnapshotLocked() =>
        new(_workspaces.ToArray(), _activeWorkspaceId);

    private WorkspaceState CreatePersistenceSnapshot()
    {
        lock (_gate)
        {
            return new WorkspaceState(_activeWorkspaceId, _workspaces.ToArray());
        }
    }

    private void PersistAndRaise(WorkspaceSnapshot snapshot)
    {
        _saveQueue.RequestSave();
        RaiseChanged(snapshot);
    }

    private void RaiseChanged(WorkspaceSnapshot snapshot) =>
        Changed?.Invoke(this, new WorkspacesChangedEventArgs(snapshot));

    private void EnsureReadyLocked()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Workspace service has not been initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed record WorkspaceSnapshot(
    IReadOnlyList<WorkspaceDefinition> Workspaces,
    Guid ActiveWorkspaceId)
{
    public WorkspaceDefinition ActiveWorkspace =>
        Workspaces.First(workspace => workspace.Id == ActiveWorkspaceId);
}

public sealed record WorkspaceDeleteResult(
    WorkspaceDefinition RemovedWorkspace,
    Guid ActiveWorkspaceId);

public sealed class WorkspacesChangedEventArgs : EventArgs
{
    public WorkspacesChangedEventArgs(WorkspaceSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public WorkspaceSnapshot Snapshot { get; }
}
