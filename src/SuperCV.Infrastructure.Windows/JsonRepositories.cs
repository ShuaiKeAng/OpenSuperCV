using SuperCV.Application.Ports;
using SuperCV.Domain.Bookmarks;
using SuperCV.Domain.Clipboard;
using SuperCV.Domain.History;
using SuperCV.Domain.Instructions;
using SuperCV.Domain.Settings;
using SuperCV.Domain.Workspaces;

namespace SuperCV.Infrastructure.Windows;

public sealed class JsonHistoryRepository : IHistoryRepository
{
    private const int CurrentSchemaVersion = 3;
    private const int ItemSchemaVersion = 1;
    private readonly V2Paths _paths;
    private readonly AtomicJsonFileStore _fileStore;

    public JsonHistoryRepository()
        : this(V2Paths.ForCurrentUser(), new AtomicJsonFileStore())
    {
    }

    public JsonHistoryRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory), new AtomicJsonFileStore())
    {
    }

    internal JsonHistoryRepository(V2Paths paths, AtomicJsonFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileStore);

        _paths = paths;
        _fileStore = fileStore;
    }

    public async ValueTask<IReadOnlyList<ClipboardEntry>> LoadAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        string historyPath = _paths.GetWorkspaceHistoryFilePath(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();
        int? schemaVersion = TryReadSchemaVersion(historyPath);
        if (schemaVersion is null)
        {
            return Array.Empty<ClipboardEntry>();
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            ClipboardEntry[]? legacyEntries = await CreateLegacyRepository(workspaceId)
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            ClipboardEntry[] migrated = legacyEntries ?? [];
            await SaveAsync(workspaceId, migrated, cancellationToken).ConfigureAwait(false);
            // Reload the just-created index so callers immediately receive disk-backed payloads,
            // rather than keeping the migration's full bodies alive until the next launch.
            return await LoadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        }

        HistoryIndex? index = await CreateIndexRepository(workspaceId)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (index?.Entries is not { Length: > 0 })
        {
            return Array.Empty<ClipboardEntry>();
        }

        ClipboardEntry[] entries = index.Entries
            .Where(item => item is not null && item.Id != Guid.Empty)
            .Select(item => new ClipboardEntry(
                item.Id,
                item.CapturedAtUtc,
                ClipboardPayload.FromDeferredFormats(
                    () => LoadItemPayload(item.FileName, workspaceId),
                    item.Formats ?? []),
                item.Tag,
                item.IsPinned))
            .ToArray();
        return Array.AsReadOnly(entries);
    }

    public async ValueTask SaveAsync(
        Guid workspaceId,
        IReadOnlyList<ClipboardEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ClipboardEntry[] snapshot = RepositoryValidation.CreateSnapshot(
            entries,
            static entry => entry.Id,
            nameof(entries));
        string itemsDirectory = GetItemsDirectory(workspaceId);
        Directory.CreateDirectory(itemsDirectory);
        var indexEntries = new HistoryIndexItem[snapshot.Length];
        foreach ((ClipboardEntry entry, int index) in snapshot.Select((entry, index) => (entry, index)))
        {
            string fileName = entry.Id.ToString("N") + ".json";
            string itemPath = Path.Combine(itemsDirectory, fileName);
            // A deferred payload already belongs to this workspace's item store.  Rewriting it
            // would first materialize the full body, defeating the bounded-memory save path.
            if (!entry.Payload.IsDeferred || !File.Exists(itemPath))
            {
                await CreateItemRepository(workspaceId, fileName)
                    .SaveAsync(entry.Payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            indexEntries[index] = new HistoryIndexItem(
                entry.Id,
                entry.CapturedAtUtc,
                entry.Tag,
                entry.IsPinned,
                fileName,
                entry.Payload.FormatKeys.ToArray(),
                CreateIndexPreview(entry.Payload));
        }

        await CreateIndexRepository(workspaceId)
            .SaveAsync(new HistoryIndex { Entries = indexEntries }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        string historyPath = _paths.GetWorkspaceHistoryFilePath(workspaceId);
        await _fileStore.DeleteAsync(historyPath, cancellationToken).ConfigureAwait(false);

        string itemsDirectory = GetItemsDirectory(workspaceId);
        if (Directory.Exists(itemsDirectory))
        {
            Directory.Delete(itemsDirectory, recursive: true);
        }

        string? workspaceDirectory = Path.GetDirectoryName(historyPath);
        if (workspaceDirectory is not null &&
            Directory.Exists(workspaceDirectory) &&
            !Directory.EnumerateFileSystemEntries(workspaceDirectory).Any())
        {
            Directory.Delete(workspaceDirectory);
        }
    }

    public ValueTask<IReadOnlyList<ClipboardEntry>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(WorkspaceDefinition.DefaultWorkspaceId, cancellationToken);

    public ValueTask SaveAsync(
        IReadOnlyList<ClipboardEntry> entries,
        CancellationToken cancellationToken = default) =>
        SaveAsync(WorkspaceDefinition.DefaultWorkspaceId, entries, cancellationToken);

    private VersionedJsonRepository<ClipboardEntry[]> CreateLegacyRepository(Guid workspaceId) =>
        new(
            _fileStore,
            _paths.GetWorkspaceHistoryFilePath(workspaceId),
            2,
            static entries => RepositoryValidation.HasUniqueIds(entries, static entry => entry.Id));

    private VersionedJsonRepository<HistoryIndex> CreateIndexRepository(Guid workspaceId) =>
        new(_fileStore, _paths.GetWorkspaceHistoryFilePath(workspaceId), CurrentSchemaVersion);

    private VersionedJsonRepository<ClipboardPayload> CreateItemRepository(Guid workspaceId, string fileName) =>
        new(_fileStore, Path.Combine(GetItemsDirectory(workspaceId), fileName), ItemSchemaVersion);

    private IReadOnlyDictionary<SuperCV.Domain.Clipboard.ClipboardFormat, string> LoadItemPayload(
        string fileName,
        Guid workspaceId)
    {
        ClipboardPayload? payload = CreateItemRepository(workspaceId, fileName)
            .LoadAsync()
            .GetAwaiter()
            .GetResult();
        return payload?.Formats ?? new Dictionary<SuperCV.Domain.Clipboard.ClipboardFormat, string>();
    }

    private string GetItemsDirectory(Guid workspaceId) => Path.Combine(
        Path.GetDirectoryName(_paths.GetWorkspaceHistoryFilePath(workspaceId))!,
        "history-items");

    private static string CreateIndexPreview(ClipboardPayload payload)
    {
        // The index is metadata only.  Existing deferred entries keep their previously stored
        // body on disk; reading that body merely to refresh a non-essential preview would make
        // every history mutation proportional to the entire history size.
        if (payload.IsDeferred)
        {
            return string.Empty;
        }

        string value = payload.IsImage ? payload.ImageLink : payload.PrimaryText;
        return value.Length <= 1024 ? value : value[..1024];
    }

    private static int? TryReadSchemaVersion(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, FileOptions.SequentialScan);
            var readerState = new System.Text.Json.JsonReaderState();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var reader = new System.Text.Json.Utf8JsonReader(buffer.AsSpan(0, bytesRead), isFinalBlock: false, readerState);
                while (reader.Read())
                {
                    if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName &&
                        reader.ValueTextEquals("schemaVersion"))
                    {
                        if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.Number)
                            return null;
                        return reader.GetInt32();
                    }
                }
                readerState = reader.CurrentState;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (System.Text.Json.JsonException) { return CurrentSchemaVersion; }
        catch (IOException) { return CurrentSchemaVersion; }
        return null;
    }

    private sealed class HistoryIndex
    {
        public HistoryIndexItem[] Entries { get; init; } = [];
    }

    private sealed record HistoryIndexItem(
        Guid Id,
        DateTimeOffset CapturedAtUtc,
        int Tag,
        bool IsPinned,
        string FileName,
        SuperCV.Domain.Clipboard.ClipboardFormat[]? Formats,
        string? Preview = null);

}

public sealed class JsonWorkspaceRepository : IWorkspaceRepository
{
    private const int CurrentSchemaVersion = 1;
    private readonly VersionedJsonRepository<WorkspaceState> _repository;

    public JsonWorkspaceRepository()
        : this(V2Paths.ForCurrentUser(), new AtomicJsonFileStore())
    {
    }

    public JsonWorkspaceRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory), new AtomicJsonFileStore())
    {
    }

    internal JsonWorkspaceRepository(V2Paths paths, AtomicJsonFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileStore);

        _repository = new VersionedJsonRepository<WorkspaceState>(
            fileStore,
            paths.WorkspacesFilePath,
            CurrentSchemaVersion,
            RepositoryValidation.IsValidWorkspaceState);
    }

    public ValueTask<WorkspaceState?> LoadAsync(CancellationToken cancellationToken = default) =>
        new(_repository.LoadAsync(cancellationToken));

    public async ValueTask SaveAsync(
        WorkspaceState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!RepositoryValidation.IsValidWorkspaceState(state))
        {
            throw new ArgumentException("The workspace state is invalid.", nameof(state));
        }

        var snapshot = new WorkspaceState(
            state.ActiveWorkspaceId,
            state.Workspaces.ToArray());
        await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class JsonBookmarkRepository : IBookmarkRepository
{
    private const int CurrentSchemaVersion = 2;
    private readonly VersionedJsonRepository<Bookmark[]> _repository;

    public JsonBookmarkRepository()
        : this(V2Paths.ForCurrentUser(), new AtomicJsonFileStore())
    {
    }

    public JsonBookmarkRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory), new AtomicJsonFileStore())
    {
    }

    internal JsonBookmarkRepository(V2Paths paths, AtomicJsonFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileStore);

        _repository = new VersionedJsonRepository<Bookmark[]>(
            fileStore,
            paths.BookmarksFilePath,
            CurrentSchemaVersion,
            static bookmarks => RepositoryValidation.HasUniqueIds(bookmarks, static bookmark => bookmark.Id));
    }

    public async ValueTask<IReadOnlyList<Bookmark>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        Bookmark[]? bookmarks = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        return bookmarks is null
            ? Array.Empty<Bookmark>()
            : Array.AsReadOnly(bookmarks);
    }

    public async ValueTask SaveAsync(
        IReadOnlyList<Bookmark> bookmarks,
        CancellationToken cancellationToken = default)
    {
        Bookmark[] snapshot = RepositoryValidation.CreateSnapshot(
            bookmarks,
            static bookmark => bookmark.Id,
            nameof(bookmarks));
        await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class JsonSettingsRepository : ISettingsRepository
{
    private const int CurrentSchemaVersion = 2;
    private readonly VersionedJsonRepository<AppSettings> _repository;

    public JsonSettingsRepository()
        : this(V2Paths.ForCurrentUser(), new AtomicJsonFileStore())
    {
    }

    public JsonSettingsRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory), new AtomicJsonFileStore())
    {
    }

    internal JsonSettingsRepository(V2Paths paths, AtomicJsonFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileStore);

        _repository = new VersionedJsonRepository<AppSettings>(
            fileStore,
            paths.SettingsFilePath,
            CurrentSchemaVersion);
    }

    public async ValueTask<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings? settings = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        return settings?.Normalize();
    }

    public async ValueTask SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _repository.SaveAsync(settings.Normalize(), cancellationToken).ConfigureAwait(false);
    }
}

internal static class RepositoryValidation
{
    internal static bool IsValidWorkspaceState(WorkspaceState state)
    {
        if (state is null ||
            state.ActiveWorkspaceId == Guid.Empty ||
            state.Workspaces is null ||
            state.Workspaces.Length == 0 ||
            !HasUniqueIds(state.Workspaces, static workspace => workspace.Id) ||
            !state.Workspaces.Any(workspace => workspace.Id == state.ActiveWorkspaceId))
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceDefinition workspace in state.Workspaces)
        {
            string name = workspace.Name ?? string.Empty;
            if (name.Length == 0 ||
                name.Length > SuperCV.Application.Workspaces.WorkspaceService.MaximumNameLength ||
                !string.Equals(name, name.Trim(), StringComparison.Ordinal) ||
                !names.Add(name))
            {
                return false;
            }
        }

        return true;
    }

    internal static T[] CreateSnapshot<T>(
        IReadOnlyList<T> values,
        Func<T, Guid> idSelector,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        ArgumentNullException.ThrowIfNull(idSelector);

        T[] snapshot = values.ToArray();
        if (!HasUniqueIds(snapshot, idSelector))
        {
            throw new ArgumentException(
                "Repository values must be non-null and have unique, non-empty identifiers.",
                parameterName);
        }

        return snapshot;
    }

    internal static bool HasUniqueIds<T>(IReadOnlyList<T> values, Func<T, Guid> idSelector)
        where T : class
    {
        var ids = new HashSet<Guid>();
        foreach (T? value in values)
        {
            if (value is null)
            {
                return false;
            }

            Guid id = idSelector(value);
            if (id == Guid.Empty || !ids.Add(id))
            {
                return false;
            }
        }

        return true;
    }
}
