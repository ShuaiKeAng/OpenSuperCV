namespace SuperCV.Infrastructure.Windows;

internal sealed class V2Paths
{
    internal const string HistoryFileName = "history.json";
    internal const string PersistentHistoryFileName = "persistent-history.sqlite3";
    internal const string WorkspacesFileName = "workspaces.json";
    internal const string WorkspacesDirectoryName = "workspaces";
    internal const string BookmarksFileName = "bookmarks.json";
    internal const string InstructionsDirectoryName = "instructions";
    internal const string LegacyInstructionsFileName = "instructions.json";
    internal const string SettingsFileName = "settings.json";
    internal const string CredentialFileName = "credential.bin";
    internal const string AiTokenUsageFileName = "ai-token-usage.json";

    private V2Paths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        WorkspacesDirectoryPath = Path.Combine(RootDirectory, WorkspacesDirectoryName);
        HistoryFilePath = GetWorkspaceHistoryFilePath(
            SuperCV.Domain.Workspaces.WorkspaceDefinition.DefaultWorkspaceId);
        WorkspacesFilePath = Path.Combine(RootDirectory, WorkspacesFileName);
        BookmarksFilePath = Path.Combine(RootDirectory, BookmarksFileName);
        InstructionsDirectoryPath = Path.Combine(RootDirectory, InstructionsDirectoryName);
        LegacyInstructionsFilePath = Path.Combine(RootDirectory, LegacyInstructionsFileName);
        SettingsFilePath = Path.Combine(RootDirectory, SettingsFileName);
        CredentialFilePath = Path.Combine(RootDirectory, CredentialFileName);
        AiTokenUsageFilePath = Path.Combine(RootDirectory, AiTokenUsageFileName);
        ImageCacheDirectoryPath = Path.Combine(RootDirectory, "images");
    }

    internal string RootDirectory { get; }

    internal string WorkspacesDirectoryPath { get; }

    internal string HistoryFilePath { get; }

    internal string WorkspacesFilePath { get; }

    internal string BookmarksFilePath { get; }

    internal string InstructionsDirectoryPath { get; }

    internal string LegacyInstructionsFilePath { get; }

    internal string SettingsFilePath { get; }

    internal string CredentialFilePath { get; }

    internal string AiTokenUsageFilePath { get; }

    internal string ImageCacheDirectoryPath { get; }

    internal string GetWorkspaceHistoryFilePath(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        return Path.Combine(
            WorkspacesDirectoryPath,
            workspaceId.ToString("N"),
            HistoryFileName);
    }

    internal string GetWorkspacePersistentHistoryFilePath(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }

        return Path.Combine(
            WorkspacesDirectoryPath,
            workspaceId.ToString("N"),
            PersistentHistoryFileName);
    }

    internal static V2Paths ForCurrentUser()
    {
        return new V2Paths(GetCurrentUserApplicationDirectory());
    }

    internal static string GetLegacyDataRoot() =>
        Path.Combine(GetCurrentUserApplicationDirectory(), "V2");

    internal static string GetCurrentUserApplicationDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's LocalApplicationData path is unavailable.");
        }

        return Path.Combine(localApplicationData, "SuperCV");
    }

    internal static V2Paths FromRootDirectory(string rootDirectory) => new(rootDirectory);
}
