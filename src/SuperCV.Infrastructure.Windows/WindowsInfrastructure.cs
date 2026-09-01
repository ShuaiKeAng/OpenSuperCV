using SuperCV.Application.Ports;
using SuperCV.Infrastructure.Windows.Platform;

namespace SuperCV.Infrastructure.Windows;

public sealed class WindowsInfrastructureServices : IAsyncDisposable
{
    internal WindowsInfrastructureServices(
        IHistoryRepository history,
        IPersistentHistoryRepository persistentHistory,
        IBookmarkRepository bookmarks,
        IInstructionRepository instructions,
        ISettingsRepository settings,
        IWorkspaceRepository workspaces,
        ICredentialStore credentials,
        AiTokenUsageLedger aiTokenUsage,
        IAiClient ai,
        IClipboardService clipboard,
        IHotkeyService hotkeys,
        IFocusService focus,
        IStartupRegistrationService startupRegistration)
    {
        History = history;
        PersistentHistory = persistentHistory;
        Bookmarks = bookmarks;
        Instructions = instructions;
        Settings = settings;
        Workspaces = workspaces;
        Credentials = credentials;
        AiTokenUsage = aiTokenUsage;
        Ai = ai;
        Clipboard = clipboard;
        Hotkeys = hotkeys;
        Focus = focus;
        StartupRegistration = startupRegistration;
    }

    public IHistoryRepository History { get; }

    public IPersistentHistoryRepository PersistentHistory { get; }

    public IBookmarkRepository Bookmarks { get; }

    public IInstructionRepository Instructions { get; }

    public ISettingsRepository Settings { get; }

    public IWorkspaceRepository Workspaces { get; }

    public ICredentialStore Credentials { get; }

    public AiTokenUsageLedger AiTokenUsage { get; }

    public IAiClient Ai { get; }

    /// <summary>
    /// Supplies the live application setting that caps every model input.
    /// </summary>
    public void ConfigureMaximumAiContextTokens(Func<int> maximumContextTokensProvider)
    {
        ArgumentNullException.ThrowIfNull(maximumContextTokensProvider);
        if (Ai is not OpenAiCompatibleClient client)
        {
            throw new InvalidOperationException("The configured AI client does not support context limits.");
        }

        client.SetMaximumContextTokensProvider(maximumContextTokensProvider);
    }

    public IClipboardService Clipboard { get; }

    public IHotkeyService Hotkeys { get; }

    public IFocusService Focus { get; }

    public IStartupRegistrationService StartupRegistration { get; }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? errors = null;

        try
        {
            await Hotkeys.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors = [exception];
        }

        try
        {
            await Clipboard.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors ??= [];
            errors.Add(exception);
        }

        if (errors is { Count: > 0 })
        {
            throw new AggregateException("One or more Windows infrastructure services failed to stop.", errors);
        }
    }
}

public static class WindowsInfrastructure
{
    public static string GetDefaultDataRoot() => V2Paths.ForCurrentUser().RootDirectory;

    public static string GetLegacyDataRoot() => V2Paths.GetLegacyDataRoot();

    public static bool HasWorkspaceCatalog(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string path = V2Paths.FromRootDirectory(rootDirectory).WorkspacesFilePath;
        return File.Exists(path) ||
               File.Exists(DurableFileCommitter.GetBackupPath(path));
    }

    public static WindowsInfrastructureServices CreateDefault() =>
        CreateCore(rootDirectory: null, httpClient: null, requestTimeout: null);

    public static WindowsInfrastructureServices Create(
        string rootDirectory,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return CreateCore(rootDirectory, httpClient, requestTimeout);
    }

    private static WindowsInfrastructureServices CreateCore(
        string? rootDirectory,
        HttpClient? httpClient,
        TimeSpan? requestTimeout)
    {
        V2Paths paths = rootDirectory is null
            ? V2Paths.ForCurrentUser()
            : V2Paths.FromRootDirectory(rootDirectory);
        IHistoryRepository history = rootDirectory is null
            ? new JsonHistoryRepository()
            : new JsonHistoryRepository(rootDirectory);
        IPersistentHistoryRepository persistentHistory = rootDirectory is null
            ? new SqlitePersistentHistoryRepository()
            : new SqlitePersistentHistoryRepository(rootDirectory);
        IBookmarkRepository bookmarks = rootDirectory is null
            ? new JsonBookmarkRepository()
            : new JsonBookmarkRepository(rootDirectory);
        IInstructionRepository instructions = rootDirectory is null
            ? new MarkdownInstructionRepository()
            : new MarkdownInstructionRepository(rootDirectory);
        ISettingsRepository settings = rootDirectory is null
            ? new JsonSettingsRepository()
            : new JsonSettingsRepository(rootDirectory);
        IWorkspaceRepository workspaces = rootDirectory is null
            ? new JsonWorkspaceRepository()
            : new JsonWorkspaceRepository(rootDirectory);
        ICredentialStore credentials = rootDirectory is null
            ? new DpapiCredentialStore()
            : new DpapiCredentialStore(rootDirectory);
        var aiTokenUsage = new AiTokenUsageLedger(paths.AiTokenUsageFilePath);
        IAiClient ai = httpClient is not null
            ? new OpenAiCompatibleClient(httpClient, requestTimeout, aiTokenUsage)
            : requestTimeout.HasValue
                ? new OpenAiCompatibleClient(requestTimeout.Value, aiTokenUsage)
                : new OpenAiCompatibleClient(aiTokenUsage);
        IClipboardService clipboard = new WindowsClipboardService(paths.ImageCacheDirectoryPath);
        IHotkeyService hotkeys = new WindowsHotkeyService();
        IFocusService focus = new WindowsFocusService();
        IStartupRegistrationService startupRegistration = new WindowsStartupRegistrationService();

        return new WindowsInfrastructureServices(
            history,
            persistentHistory,
            bookmarks,
            instructions,
            settings,
            workspaces,
            credentials,
            aiTokenUsage,
            ai,
            clipboard,
            hotkeys,
            focus,
            startupRegistration);
    }
}
