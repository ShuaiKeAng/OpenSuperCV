using SuperCV.Domain.Bookmarks;
using SuperCV.Domain.History;
using SuperCV.Domain.Instructions;
using SuperCV.Domain.Settings;
using SuperCV.Domain.Workspaces;
using SuperCV.Application.History;

namespace SuperCV.Application.Ports;

public interface IHistoryRepository
{
    ValueTask<IReadOnlyList<ClipboardEntry>> LoadAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        Guid workspaceId,
        IReadOnlyList<ClipboardEntry> entries,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public interface IPersistentHistoryRepository : IAsyncDisposable
{
    ValueTask AppendRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken = default);

    ValueTask SeedOnceAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken = default);

    ValueTask<PersistentHistoryPage> QueryAsync(
        Guid workspaceId,
        string searchText,
        int limit,
        PersistentHistoryCursor? cursor = null,
        PersistentHistoryDateRange? dateRange = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        Guid workspaceId,
        long storageKey,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteMatchingAsync(
        Guid workspaceId,
        PersistentHistoryMatch match,
        CancellationToken cancellationToken = default);

    ValueTask<int> DeleteMatchingRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryMatch> matches,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ReplaceMatchingTextAsync(
        Guid workspaceId,
        PersistentHistoryMatch match,
        string replacementText,
        CancellationToken cancellationToken = default);

    ValueTask<int> TrimToLimitAsync(
        Guid workspaceId,
        int retainedItems,
        CancellationToken cancellationToken = default);

    ValueTask<int> DeleteImagesAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    ValueTask DeleteWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public interface IBookmarkRepository
{
    ValueTask<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        IReadOnlyList<Bookmark> bookmarks,
        CancellationToken cancellationToken = default);
}

public interface IInstructionRepository
{
    ValueTask<IReadOnlyList<CustomInstruction>> LoadAsync(CancellationToken cancellationToken = default);

    string? GetDocumentPath(Guid id);

    ValueTask<CustomInstruction?> RepairDocumentAsync(
        CustomInstruction expected,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        IReadOnlyList<CustomInstruction> instructions,
        CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    ValueTask<AppSettings?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IWorkspaceRepository
{
    ValueTask<WorkspaceState?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        WorkspaceState state,
        CancellationToken cancellationToken = default);
}

public interface ICredentialStore
{
    ValueTask<string?> ReadAsync(
        string name,
        Guid revision,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        string name,
        Guid revision,
        string secret,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        string name,
        Guid revision,
        CancellationToken cancellationToken = default);
}
