using SuperCV.Application;
using SuperCV.Application.Bookmarks;
using SuperCV.Application.History;
using SuperCV.Application.Instructions;
using SuperCV.Application.Ports;
using SuperCV.Application.Settings;
using SuperCV.Application.Workspaces;
using SuperCV.Domain.History;
using SuperCV.Domain.Instructions;
using SuperCV.Domain.Workspaces;
using SuperCV.Infrastructure.Windows;
using System.Diagnostics;
using System.IO;

namespace SuperCV;

internal sealed class AppRuntime : IAsyncDisposable
{
    private const string DataRootEnvironmentVariable = "SUPERCV_DATA_ROOT";
    private const string LegacyDataRootEnvironmentVariable = "SUPERCV_V2_DATA_ROOT";
    private readonly SemaphoreSlim _imageCleanupGate = new(1, 1);
    private readonly object _imageMaintenanceTaskGate = new();
    private HashSet<string> _activeImageLinks = new(StringComparer.OrdinalIgnoreCase);
    private Task _pendingImageMaintenance = Task.CompletedTask;
    private bool _imageSupportEnabled;
    private int _removingAllImages;

    private AppRuntime(
        string dataRoot,
        WindowsInfrastructureServices infrastructure,
        HistoryService history,
        PersistentHistoryService persistentHistory,
        BookmarksService bookmarks,
        InstructionsService instructions,
        SettingsService settings,
        WorkspaceService workspaces)
    {
        DataTransfer = new DataTransferService(dataRoot);
        Infrastructure = infrastructure;
        History = history;
        PersistentHistory = persistentHistory;
        Bookmarks = bookmarks;
        Instructions = instructions;
        Settings = settings;
        Workspaces = workspaces;
        _imageSupportEnabled = Settings.Snapshot.EnableImageSupport;
        Clipboard.ImageCaptureEnabled = _imageSupportEnabled;
        Settings.Changed += OnSettingsChanged;
        History.Changed += OnHistoryChanged;
    }

    internal WindowsInfrastructureServices Infrastructure { get; }

    internal DataTransferService DataTransfer { get; }

    internal HistoryService History { get; }

    internal PersistentHistoryService PersistentHistory { get; }

    internal BookmarksService Bookmarks { get; }

    internal InstructionsService Instructions { get; }

    internal SettingsService Settings { get; }

    internal WorkspaceService Workspaces { get; }

    internal IClipboardService Clipboard => Infrastructure.Clipboard;

    internal IHotkeyService Hotkeys => Infrastructure.Hotkeys;

    internal IFocusService Focus => Infrastructure.Focus;

    internal IStartupRegistrationService StartupRegistration => Infrastructure.StartupRegistration;

    internal IAiClient Ai => Infrastructure.Ai;

    internal AiTokenUsageLedger AiTokenUsage => Infrastructure.AiTokenUsage;

    internal static string ResolveDataRoot()
    {
        string? configuredRoot = GetDataRootEnvironmentOverride();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        }

        DataRootLocationStore locationStore = DataRootLocationStore.ForCurrentUser();
        string? selectedRoot = locationStore.TryGetConfiguredDataRoot();
        if (!string.IsNullOrWhiteSpace(selectedRoot))
        {
            return selectedRoot;
        }

        string defaultRoot = WindowsInfrastructure.GetDefaultDataRoot();
        if (WindowsInfrastructure.HasWorkspaceCatalog(defaultRoot))
        {
            return defaultRoot;
        }

        string legacyRoot = WindowsInfrastructure.GetLegacyDataRoot();
        return WindowsInfrastructure.HasWorkspaceCatalog(legacyRoot)
            ? legacyRoot
            : defaultRoot;
    }

    internal static bool IsDataRootEnvironmentOverrideActive() =>
        !string.IsNullOrWhiteSpace(GetDataRootEnvironmentOverride());

    private static string? GetDataRootEnvironmentOverride()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? Environment.GetEnvironmentVariable(LegacyDataRootEnvironmentVariable)
            : configuredRoot;
    }

    internal static async ValueTask<AppRuntime> CreateAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        bool isFirstUse = !WindowsInfrastructure.HasWorkspaceCatalog(dataRoot);
        WindowsInfrastructureServices infrastructure = WindowsInfrastructure.Create(dataRoot);
        var settings = new SettingsService(infrastructure.Settings, infrastructure.Credentials);
        try
        {
            await settings.InitializeAsync(cancellationToken).ConfigureAwait(false);
            infrastructure.ConfigureMaximumAiContextTokens(
                () => settings.Snapshot.MaxAiContextTokens);
        }
        catch (Exception startupException)
        {
            var cleanupFailures = new List<Exception>();
            await DisposeOneAsync(settings, cleanupFailures).ConfigureAwait(false);
            await DisposeOneAsync(infrastructure, cleanupFailures).ConfigureAwait(false);
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Settings initialization failed and cleanup was incomplete.",
                    new[] { startupException }.Concat(cleanupFailures));
            }

            throw;
        }

        var workspaces = new WorkspaceService(infrastructure.Workspaces);
        try
        {
            await workspaces.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception startupException)
        {
            var cleanupFailures = new List<Exception>();
            await DisposeOneAsync(workspaces, cleanupFailures).ConfigureAwait(false);
            await DisposeOneAsync(settings, cleanupFailures).ConfigureAwait(false);
            await DisposeOneAsync(infrastructure, cleanupFailures).ConfigureAwait(false);
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "Workspace initialization failed and cleanup was incomplete.",
                    new[] { startupException }.Concat(cleanupFailures));
            }

            throw;
        }

        var settingsSnapshot = settings.Snapshot;
        bool shouldSeedInstructionPresets =
            settingsSnapshot.InstructionPresetVersion <
            FirstUseDefaults.CurrentInstructionPresetVersion;
        var persistentHistory = new PersistentHistoryService(infrastructure.PersistentHistory);
        var history = new HistoryService(
            infrastructure.History,
            settingsSnapshot.MaxHistoryItems,
            settingsSnapshot.DisplayItems,
            activeWorkspaceId: workspaces.Snapshot.ActiveWorkspaceId,
            persistentHistory: persistentHistory,
            persistentHistoryEnabled: settingsSnapshot.PersistentHistoryEnabled);
        var bookmarks = new BookmarksService(infrastructure.Bookmarks);
        var instructions = new InstructionsService(infrastructure.Instructions);
        var runtime = new AppRuntime(
            dataRoot,
            infrastructure,
            history,
            persistentHistory,
            bookmarks,
            instructions,
            settings,
            workspaces);

        try
        {
            await history.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await bookmarks.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await instructions.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (settingsSnapshot.EnableImageSupport)
            {
                await runtime.CleanupUnusedImageCacheAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await runtime.RemoveAllImagesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (isFirstUse || shouldSeedInstructionPresets)
            {
                await SeedInitialContentAsync(
                        history,
                        instructions,
                        settings,
                        workspaces,
                        isFirstUse,
                        shouldSeedInstructionPresets,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return runtime;
        }
        catch (Exception startupException)
        {
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Application initialization failed and cleanup was incomplete.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    private static async ValueTask SeedInitialContentAsync(
        HistoryService history,
        InstructionsService instructions,
        SettingsService settings,
        WorkspaceService workspaces,
        bool isFirstUse,
        bool shouldSeedInstructionPresets,
        CancellationToken cancellationToken)
    {
        if (isFirstUse &&
            !history.Snapshot.Entries.Any(
                entry => string.Equals(
                    entry.Payload.PrimaryText,
                    FirstUseDefaults.WelcomeEntryText,
                    StringComparison.Ordinal)))
        {
            _ = await history.AddAsync(
                    FirstUseDefaults.CreateWelcomePayload(settings.Snapshot.Language),
                    allowDuplicate: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (shouldSeedInstructionPresets)
        {
            IReadOnlyList<CustomInstruction> presets =
                FirstUseDefaults.CreatePresetInstructions(
                    DateTimeOffset.UtcNow,
                    settings.Snapshot.Language);
            foreach (CustomInstruction preset in presets)
            {
                if (instructions.Snapshot.Any(
                        instruction =>
                            instruction.Id == preset.Id ||
                            instruction.Label.Equals(
                                preset.Label,
                                StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _ = await instructions.AddAsync(preset, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (isFirstUse)
        {
            await history.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (shouldSeedInstructionPresets)
        {
            await instructions.FlushAsync(cancellationToken).ConfigureAwait(false);
            await settings.UpdateAndPersistAsync(
                    current => current with
                    {
                        InstructionPresetVersion =
                            FirstUseDefaults.CurrentInstructionPresetVersion,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (isFirstUse)
        {
            // The workspace catalog remains the durable first-use marker and is committed last.
            await workspaces.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask FlushForExportAsync(CancellationToken cancellationToken = default)
    {
        await History.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Bookmarks.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Instructions.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Workspaces.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Settings.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        Settings.Changed -= OnSettingsChanged;
        History.Changed -= OnHistoryChanged;
        Task pendingImageMaintenance;
        lock (_imageMaintenanceTaskGate)
        {
            pendingImageMaintenance = _pendingImageMaintenance;
        }

        try
        {
            await pendingImageMaintenance.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await DisposeOneAsync(Instructions, failures).ConfigureAwait(false);
        await DisposeOneAsync(Bookmarks, failures).ConfigureAwait(false);
        await DisposeOneAsync(History, failures).ConfigureAwait(false);
        await DisposeOneAsync(PersistentHistory, failures).ConfigureAwait(false);
        await DisposeOneAsync(Workspaces, failures).ConfigureAwait(false);
        await DisposeOneAsync(Settings, failures).ConfigureAwait(false);
        await DisposeOneAsync(Infrastructure, failures).ConfigureAwait(false);
        _imageCleanupGate.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more application services failed to stop cleanly.", failures);
        }
    }

    private static async ValueTask DisposeOneAsync(IAsyncDisposable service, List<Exception> failures)
    {
        try
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        History.SetMaxItems(e.Settings.MaxHistoryItems);
        History.SetPageSize(e.Settings.DisplayItems);
        History.SetPersistentHistoryEnabled(e.Settings.PersistentHistoryEnabled);
        bool wasEnabled = _imageSupportEnabled;
        _imageSupportEnabled = e.Settings.EnableImageSupport;
        Clipboard.ImageCaptureEnabled = _imageSupportEnabled;
        if (wasEnabled && !_imageSupportEnabled)
        {
            Clipboard.ImageCaptureEnabled = false;
            TrackImageMaintenance(RemoveAllImagesAsync());
        }
    }

    private void OnHistoryChanged(object? sender, HistoryChangedEventArgs e)
    {
        HashSet<string> currentLinks = e.Snapshot.Entries
            .Where(entry => entry.Payload.IsImage)
            .Select(entry => entry.Payload.ImageLink)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool removedImage = _activeImageLinks.Except(currentLinks).Any();
        _activeImageLinks = currentLinks;
        if (!removedImage || Volatile.Read(ref _removingAllImages) != 0)
        {
            return;
        }

        TrackImageMaintenance(CleanupUnusedImageCacheAsync());
    }

    private void TrackImageMaintenance(ValueTask operation)
    {
        Task task = operation.AsTask();
        _ = task.ContinueWith(
            failed => Debug.WriteLine(failed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        lock (_imageMaintenanceTaskGate)
        {
            _pendingImageMaintenance = Task.WhenAll(_pendingImageMaintenance, task);
        }
    }

    private async ValueTask RemoveAllImagesAsync(CancellationToken cancellationToken = default)
    {
        await _imageCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _removingAllImages, 1);
        try
        {
            Clipboard.ImageCaptureEnabled = false;
            Guid activeWorkspaceId = Workspaces.Snapshot.ActiveWorkspaceId;
            await History.RemoveImagesAsync(cancellationToken).ConfigureAwait(false);
            await History.FlushAsync(cancellationToken).ConfigureAwait(false);

            foreach (WorkspaceDefinition workspace in Workspaces.Snapshot.Workspaces)
            {
                if (workspace.Id != activeWorkspaceId)
                {
                    IReadOnlyList<ClipboardEntry> entries =
                        await Infrastructure.History.LoadAsync(workspace.Id, cancellationToken)
                            .ConfigureAwait(false);
                    ClipboardEntry[] textEntries = entries
                        .Where(entry => !entry.Payload.IsImage)
                        .ToArray();
                    if (textEntries.Length != entries.Count)
                    {
                        await Infrastructure.History.SaveAsync(
                                workspace.Id,
                                textEntries,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                _ = await PersistentHistory.DeleteImagesAsync(
                        workspace.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await Clipboard.DeleteCachedImagesExceptAsync(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _removingAllImages, 0);
            Clipboard.ImageCaptureEnabled = _imageSupportEnabled;
            _imageCleanupGate.Release();
        }
    }

    internal async ValueTask SetImageSupportEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        _imageSupportEnabled = enabled;
        Clipboard.ImageCaptureEnabled = enabled;
        if (!enabled)
        {
            await RemoveAllImagesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask CleanupUnusedImageCacheAsync(
        CancellationToken cancellationToken = default)
    {
        await _imageCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await History.FlushAsync(cancellationToken).ConfigureAwait(false);
            // HistoryChanged can be raised while a workspace switch is in progress. In that
            // window History already points at the destination workspace, while the workspace
            // catalog still reports the source workspace as active. Always load the persisted
            // history by workspace id so a switch cannot make the source workspace's images look
            // orphaned and delete their shared cache files.
            WorkspaceDefinition[] workspaces = Workspaces.Snapshot.Workspaces.ToArray();
            var retainedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkspaceDefinition workspace in workspaces)
            {
                IReadOnlyList<ClipboardEntry> entries =
                    await Infrastructure.History.LoadAsync(workspace.Id, cancellationToken)
                        .ConfigureAwait(false);
                foreach (string link in entries
                             .Where(entry => entry.Payload.IsImage)
                             .Select(entry => entry.Payload.ImageLink)
                             .OfType<string>())
                {
                    retainedLinks.Add(link);
                }
            }

            await Clipboard.DeleteCachedImagesExceptAsync(retainedLinks, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _imageCleanupGate.Release();
        }
    }

    internal async ValueTask<int> TrimPersistentHistoryToLimitAsync(
        int retainedItems,
        CancellationToken cancellationToken = default)
    {
        int removedCount = 0;
        foreach (WorkspaceDefinition workspace in Workspaces.Snapshot.Workspaces)
        {
            removedCount += await PersistentHistory.TrimToLimitAsync(
                    workspace.Id,
                    retainedItems,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return removedCount;
    }

    internal async ValueTask SynchronizePersistentHistoryToRichLimitAsync(
        int retainedItems,
        CancellationToken cancellationToken = default)
    {
        await History.FlushAsync(cancellationToken).ConfigureAwait(false);
        foreach (WorkspaceDefinition workspace in Workspaces.Snapshot.Workspaces)
        {
            IReadOnlyList<ClipboardEntry> entries =
                await Infrastructure.History.LoadAsync(workspace.Id, cancellationToken)
                    .ConfigureAwait(false);
            await PersistentHistory.DeleteWorkspaceAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(false);
            await PersistentHistory.SeedFromRichHistoryOnceAsync(
                    workspace.Id,
                    entries,
                    cancellationToken)
                .ConfigureAwait(false);
            await PersistentHistory.TrimToLimitAsync(
                    workspace.Id,
                    retainedItems,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask SelectWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = Workspaces.Snapshot;
        if (!snapshot.Workspaces.Any(workspace => workspace.Id == workspaceId))
        {
            throw new InvalidOperationException("所选工作区不存在。");
        }

        if (snapshot.ActiveWorkspaceId == workspaceId)
        {
            return;
        }

        await History.SwitchWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        Workspaces.Select(workspaceId);
    }

    internal async ValueTask<WorkspaceDefinition> CreateWorkspaceAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        WorkspaceDefinition workspace = Workspaces.Create(name);
        await History.SwitchWorkspaceAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        return workspace;
    }

    internal WorkspaceDefinition RenameWorkspace(Guid workspaceId, string name) =>
        Workspaces.Rename(workspaceId, name);

    internal async ValueTask DeleteWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSnapshot snapshot = Workspaces.Snapshot;
        int index = -1;
        for (int workspaceIndex = 0; workspaceIndex < snapshot.Workspaces.Count; workspaceIndex++)
        {
            if (snapshot.Workspaces[workspaceIndex].Id == workspaceId)
            {
                index = workspaceIndex;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("要删除的工作区不存在。");
        }

        if (snapshot.Workspaces.Count == 1)
        {
            throw new InvalidOperationException("至少需要保留一个工作区。");
        }

        Guid replacementId = snapshot.ActiveWorkspaceId;
        if (replacementId == workspaceId)
        {
            WorkspaceDefinition[] remaining = snapshot.Workspaces
                .Where(workspace => workspace.Id != workspaceId)
                .ToArray();
            replacementId = remaining[Math.Min(index, remaining.Length - 1)].Id;
        }

        await PersistentHistory.DeleteWorkspaceAsync(workspaceId, cancellationToken)
            .ConfigureAwait(false);
        await History.DeleteWorkspaceAsync(
                workspaceId,
                replacementId,
                cancellationToken)
            .ConfigureAwait(false);
        _ = Workspaces.Delete(workspaceId);
        await CleanupUnusedImageCacheAsync(cancellationToken).ConfigureAwait(false);
    }

}
