using Microsoft.Win32;

namespace SuperCV.Infrastructure.Windows;

/// <summary>
/// Stores the user-selected data root outside the data root itself, so the setting survives
/// migration and can be read before application services open their data files.
/// </summary>
public sealed class DataRootLocationStore
{
    private const string CurrentUserRegistryPath = @"Software\SuperCV";
    private const string DataRootValueName = "DataRoot";
    private const string PendingSourceRootValueName = "PendingSourceRoot";
    private const string PendingTargetRootValueName = "PendingTargetRoot";
    private readonly string _registrySubKeyPath;

    internal DataRootLocationStore(string registrySubKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrySubKeyPath);
        if (Path.IsPathRooted(registrySubKeyPath))
        {
            throw new ArgumentException("注册表子项路径不能是文件系统路径。", nameof(registrySubKeyPath));
        }

        _registrySubKeyPath = registrySubKeyPath.Trim('\\');
    }

    public static DataRootLocationStore ForCurrentUser() => new(CurrentUserRegistryPath);

    public string Resolve(string defaultDataRoot)
    {
        string fallback = NormalizeDataRoot(defaultDataRoot);
        return TryGetConfiguredDataRoot() ?? fallback;
    }

    public string? TryGetConfiguredDataRoot() => TryReadRegistryPath(DataRootValueName);

    public ValueTask SaveAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteRegistryPath(DataRootValueName, NormalizeDataRoot(dataRoot));
        return ValueTask.CompletedTask;
    }

    public ValueTask ScheduleSourceDeletionAsync(
        string sourceDataRoot,
        string expectedActiveDataRoot,
        CancellationToken cancellationToken = default)
    {
        string normalizedSourceRoot = NormalizeDataRoot(sourceDataRoot);
        string normalizedTargetRoot = NormalizeDataRoot(expectedActiveDataRoot);
        if (PathsOverlap(normalizedSourceRoot, normalizedTargetRoot))
        {
            throw new ArgumentException("待删除的数据目录不能与新数据目录重叠。", nameof(sourceDataRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteRegistryPath(PendingSourceRootValueName, normalizedSourceRoot);
        WriteRegistryPath(PendingTargetRootValueName, normalizedTargetRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Removes the previous data root only after the application has restarted with a distinct,
    /// verified active root. Failures leave the marker in place for a later retry.
    /// </summary>
    public bool TryDeleteScheduledSourceData(string activeDataRoot)
    {
        PendingSourceDeletion? pendingDeletion = TryReadPendingSourceDeletion();
        if (pendingDeletion is null)
        {
            return false;
        }

        string activeRoot;
        try
        {
            activeRoot = NormalizeDataRoot(activeDataRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!string.Equals(
                pendingDeletion.ExpectedActiveRoot,
                activeRoot,
                StringComparison.OrdinalIgnoreCase) ||
            PathsOverlap(pendingDeletion.SourceRoot, activeRoot) ||
            File.Exists(pendingDeletion.SourceRoot))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(pendingDeletion.SourceRoot))
            {
                Directory.Delete(pendingDeletion.SourceRoot, recursive: true);
            }

            ClearScheduledDeletion();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private PendingSourceDeletion? TryReadPendingSourceDeletion()
    {
        string? sourceRoot = TryReadRegistryPath(PendingSourceRootValueName);
        string? targetRoot = TryReadRegistryPath(PendingTargetRootValueName);
        if (!string.IsNullOrWhiteSpace(sourceRoot) && !string.IsNullOrWhiteSpace(targetRoot))
        {
            return new PendingSourceDeletion(sourceRoot, targetRoot);
        }

        return null;
    }

    private string? TryReadRegistryPath(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registrySubKeyPath, writable: false);
            return key?.GetValue(valueName) is string value
                ? NormalizeDataRoot(value)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void WriteRegistryPath(string valueName, string dataRoot)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_registrySubKeyPath, writable: true)
            ?? throw new IOException("无法创建 SuperCV 的注册表配置项。");
        key.SetValue(valueName, dataRoot, RegistryValueKind.String);
    }

    private void ClearScheduledDeletion()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registrySubKeyPath, writable: true);
            key?.DeleteValue(PendingSourceRootValueName, throwOnMissingValue: false);
            key?.DeleteValue(PendingTargetRootValueName, throwOnMissingValue: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

    }

    internal void DeleteRegistryStateForTesting()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registrySubKeyPath, throwOnMissingSubKey: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsOverlap(string firstPath, string secondPath) =>
        string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase) ||
        IsDescendant(firstPath, secondPath) ||
        IsDescendant(secondPath, firstPath);

    private static bool IsDescendant(string parentPath, string candidatePath)
    {
        string parentWithSeparator = Path.EndsInDirectorySeparator(parentPath)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        string root = Path.GetPathRoot(normalizedRoot) ?? string.Empty;
        if (string.Equals(normalizedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("数据目录不能是磁盘根目录。", nameof(dataRoot));
        }

        return normalizedRoot;
    }

    private sealed record PendingSourceDeletion(string SourceRoot, string ExpectedActiveRoot);
}

/// <summary>
/// Copies verified SuperCV data into an empty target directory. The active data root remains
/// untouched, allowing the caller to switch to the destination only after migration succeeds.
/// </summary>
public static class DataRootMigrationService
{
    public static async ValueTask<ImportPackageInfo> MigrateAsync(
        string sourceDataRoot,
        string targetDataRoot,
        CancellationToken cancellationToken = default)
    {
        string sourceRoot = NormalizeDataRoot(sourceDataRoot);
        string targetRoot = NormalizeDataRoot(targetDataRoot);
        ValidateDistinctRoots(sourceRoot, targetRoot);
        ValidateEmptyTarget(targetRoot);

        string stagingRoot = targetRoot + ".import-staging";
        string rollbackRoot = targetRoot + ".before-import";
        if (Directory.Exists(stagingRoot) || Directory.Exists(rollbackRoot))
        {
            throw new IOException("目标目录存在未完成的迁移数据，请选择其他空目录或先处理该目录。");
        }

        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, recursive: false);
        }

        string archivePath = Path.Combine(
            Path.GetTempPath(),
            $"SuperCV-data-migration-{Guid.NewGuid():N}{DataTransferService.ArchiveExtension}");
        try
        {
            var sourceTransfer = new DataTransferService(sourceRoot);
            await sourceTransfer.ExportAsync(archivePath, cancellationToken).ConfigureAwait(false);

            var targetTransfer = new DataTransferService(targetRoot);
            ImportPackageInfo package = await targetTransfer
                .StageImportAsync(archivePath, cancellationToken)
                .ConfigureAwait(false);
            DataTransferService.ApplyPendingImport(targetRoot);
            return package;
        }
        finally
        {
            DurableFileCommitter.TryDelete(archivePath);
        }
    }

    private static void ValidateEmptyTarget(string targetRoot)
    {
        if (File.Exists(targetRoot))
        {
            throw new IOException("目标路径已被文件占用，请选择一个空文件夹。");
        }

        if (Directory.Exists(targetRoot) && Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            throw new IOException("目标文件夹必须为空，避免覆盖现有文件。");
        }
    }

    private static void ValidateDistinctRoots(string sourceRoot, string targetRoot)
    {
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("目标路径与当前数据路径相同。", nameof(targetRoot));
        }

        if (IsSameOrDescendant(sourceRoot, targetRoot) || IsSameOrDescendant(targetRoot, sourceRoot))
        {
            throw new ArgumentException("目标路径不能位于当前数据目录内，也不能包含当前数据目录。", nameof(targetRoot));
        }
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        string parentWithSeparator = Path.EndsInDirectorySeparator(parentPath)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        string root = Path.GetPathRoot(normalizedRoot) ?? string.Empty;
        if (string.Equals(normalizedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("数据目录不能是磁盘根目录。", nameof(dataRoot));
        }

        return normalizedRoot;
    }
}
