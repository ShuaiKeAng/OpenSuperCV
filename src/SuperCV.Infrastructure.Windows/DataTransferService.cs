using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SuperCV.Application.Ports;
using SuperCV.Domain.Clipboard;
using SuperCV.Domain.History;
using SuperCV.Domain.Workspaces;

namespace SuperCV.Infrastructure.Windows;

/// <summary>
/// Creates and validates the single-file SuperCV migration package.  Imports are
/// staged outside the live data root and become active only during the next start.
/// </summary>
public sealed class DataTransferService
{
    public const string ArchiveExtension = ".supercvbackup";
    private const string Format = "SuperCV.V2.DataTransfer";
    private const int FormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const string PayloadPrefix = "payload/";
    private const int MaximumEntryCount = 20_000;
    private const long MaximumUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _dataRoot;
    private readonly string _stagingRoot;

    public DataTransferService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _stagingRoot = _dataRoot + ".import-staging";
    }

    public async ValueTask ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination))
        {
            throw new IOException("目标文件已存在，请选择其他位置或文件名。");
        }

        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new IOException("无法确定导出文件夹。");
        }

        Directory.CreateDirectory(destinationDirectory);
        string temporaryPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var files = EnumerateDataFiles().ToArray();
            var manifestFiles = new List<ManifestFile>(files.Length);
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string sourcePath in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = Path.GetRelativePath(_dataRoot, sourcePath)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    string entryName = PayloadPrefix + relativePath;
                    FileInfo info = new(sourcePath);
                    string hash = await ComputeHashAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using Stream entryStream = entry.Open();
                    await using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 65_536,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                    manifestFiles.Add(new ManifestFile(entryName, info.Length, hash));
                }

                var manifest = new ArchiveManifest(
                    Format,
                    FormatVersion,
                    DateTimeOffset.UtcNow,
                    manifestFiles.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
                ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using Stream manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, destination);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Fully decodes and verifies an archive before copying it into the pending-import location.
    /// </summary>
    public async ValueTask<ImportPackageInfo> StageImportAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException("找不到导入文件。", fullArchivePath);
        }

        string temporaryRoot = _stagingRoot + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            ArchiveManifest manifest = await ExtractAndVerifyAsync(
                    fullArchivePath,
                    temporaryRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateDecodedPayloadAsync(temporaryRoot, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(_stagingRoot))
            {
                Directory.Delete(_stagingRoot, recursive: true);
            }

            Directory.Move(temporaryRoot, _stagingRoot);
            return new ImportPackageInfo(manifest.CreatedAtUtc, manifest.Files.Count);
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }

            throw;
        }
    }

    /// <summary>Activates a verified staged import before application services open their files.</summary>
    public static void ApplyPendingImport(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string liveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        string stagingRoot = liveRoot + ".import-staging";
        if (!Directory.Exists(stagingRoot))
        {
            return;
        }

        string rollbackRoot = liveRoot + ".before-import";
        if (Directory.Exists(rollbackRoot))
        {
            Directory.Delete(rollbackRoot, recursive: true);
        }

        bool liveRootMoved = false;
        try
        {
            if (Directory.Exists(liveRoot))
            {
                Directory.Move(liveRoot, rollbackRoot);
                liveRootMoved = true;
            }

            Directory.Move(stagingRoot, liveRoot);
        }
        catch
        {
            if (liveRootMoved && !Directory.Exists(liveRoot) && Directory.Exists(rollbackRoot))
            {
                Directory.Move(rollbackRoot, liveRoot);
            }

            throw;
        }
    }

    private IEnumerable<string> EnumerateDataFiles()
    {
        if (!Directory.Exists(_dataRoot))
        {
            yield break;
        }

        string[] rootFiles =
        [
            V2Paths.SettingsFileName,
            V2Paths.WorkspacesFileName,
            V2Paths.BookmarksFileName,
            V2Paths.CredentialFileName,
            V2Paths.AiTokenUsageFileName,
        ];
        foreach (string name in rootFiles)
        {
            string path = Path.Combine(_dataRoot, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (string directoryName in new[]
                 {
                     V2Paths.WorkspacesDirectoryName,
                     V2Paths.InstructionsDirectoryName,
                     "images",
                 })
        {
            string directory = Path.Combine(_dataRoot, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(path => !path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static async ValueTask<ArchiveManifest> ExtractAndVerifyAsync(
        string archivePath,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaximumEntryCount)
        {
            throw new InvalidDataException("导入文件中的条目数量不合法。");
        }

        ZipArchiveEntry? manifestEntry = archive.GetEntry(ManifestEntryName);
        if (manifestEntry is null || manifestEntry.Length is 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("导入文件缺少有效的清单。");
        }

        ArchiveManifest? manifest;
        await using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(
                manifestStream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        ValidateManifest(manifest);
        Directory.CreateDirectory(temporaryRoot);
        var expected = manifest!.Files.ToDictionary(item => item.Path, StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName == ManifestEntryName)
            {
                continue;
            }

            if (!expected.TryGetValue(entry.FullName, out ManifestFile? expectedFile) ||
                entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("导入文件包含未列入清单的内容。");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumUncompressedBytes || entry.Length != expectedFile.Length)
            {
                throw new InvalidDataException("导入文件的大小校验失败。");
            }

            string outputPath = GetSafePayloadPath(temporaryRoot, entry.FullName);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await using Stream entryStream = entry.Open();
            await using (var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await entryStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            string hash = await ComputeHashAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedFile.Sha256),
                    Convert.FromHexString(hash)))
            {
                throw new InvalidDataException("导入文件的内容校验失败，文件可能已损坏或被修改。");
            }

            expected.Remove(entry.FullName);
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException("导入文件不完整，部分清单内容缺失。");
        }

        return manifest;
    }

    private async ValueTask ValidateDecodedPayloadAsync(
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        foreach (string requiredFile in new[]
                 {
                     V2Paths.SettingsFileName,
                     V2Paths.WorkspacesFileName,
                     V2Paths.BookmarksFileName,
                 })
        {
            if (!File.Exists(Path.Combine(payloadRoot, requiredFile)))
            {
                throw new InvalidDataException($"导入文件缺少必要数据：{requiredFile}。");
            }
        }

        var settings = new JsonSettingsRepository(payloadRoot);
        var workspaces = new JsonWorkspaceRepository(payloadRoot);
        var bookmarks = new JsonBookmarkRepository(payloadRoot);
        var instructions = new MarkdownInstructionRepository(payloadRoot);
        _ = await settings.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("导入设置无法读取。");
        WorkspaceState workspaceState = await workspaces.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("导入工作区无法读取。");
        _ = await bookmarks.LoadAsync(cancellationToken).ConfigureAwait(false);
        _ = await instructions.LoadAsync(cancellationToken).ConfigureAwait(false);

        var histories = new JsonHistoryRepository(payloadRoot);
        await using var persistentHistory = new SqlitePersistentHistoryRepository(payloadRoot);
        foreach (WorkspaceDefinition workspace in workspaceState.Workspaces)
        {
            IReadOnlyList<ClipboardEntry> entries =
                await histories.LoadAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
            ClipboardEntry[] remappedEntries = RemapImportedImageLinks(entries, payloadRoot);
            if (!entries.SequenceEqual(remappedEntries))
            {
                await histories.SaveAsync(workspace.Id, remappedEntries, cancellationToken)
                    .ConfigureAwait(false);
            }

            string persistentPath = Path.Combine(
                payloadRoot,
                V2Paths.WorkspacesDirectoryName,
                workspace.Id.ToString("N"),
                V2Paths.PersistentHistoryFileName);
            // Long-term history is optional. A workspace that has never enabled it
            // legitimately has no database file; when present it must still open cleanly.
            if (File.Exists(persistentPath))
            {
                _ = await persistentHistory.QueryAsync(
                        workspace.Id,
                        string.Empty,
                        limit: 1,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private ClipboardEntry[] RemapImportedImageLinks(
        IReadOnlyList<ClipboardEntry> entries,
        string payloadRoot)
    {
        string stagedImageRoot = Path.GetFullPath(Path.Combine(payloadRoot, "images"));
        string finalImageRoot = Path.GetFullPath(Path.Combine(_dataRoot, "images"));
        var remapped = new ClipboardEntry[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            ClipboardEntry entry = entries[index];
            if (!entry.Payload.IsImage)
            {
                remapped[index] = entry;
                continue;
            }

            string fileName = Path.GetFileName(entry.Payload.ImageLink);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(fileName, entry.Payload.ImageLink.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Last(), StringComparison.Ordinal))
            {
                throw new InvalidDataException("导入图片条目的缓存路径不合法。");
            }

            string stagedImagePath = Path.GetFullPath(Path.Combine(stagedImageRoot, fileName));
            string stagedRootPrefix = Path.TrimEndingDirectorySeparator(stagedImageRoot) + Path.DirectorySeparatorChar;
            if (!stagedImagePath.StartsWith(stagedRootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(stagedImagePath))
            {
                throw new InvalidDataException("导入图片条目缺少已校验的缓存图片。");
            }

            string finalImagePath = Path.Combine(finalImageRoot, fileName);
            remapped[index] = entry with
            {
                Payload = new ClipboardPayload(new Dictionary<ClipboardFormat, string>
                {
                    [ClipboardFormat.Image] = finalImagePath,
                }),
            };
        }

        return remapped;
    }

    private static void ValidateManifest(ArchiveManifest? manifest)
    {
        if (manifest is null ||
            !string.Equals(manifest.Format, Format, StringComparison.Ordinal) ||
            manifest.Version != FormatVersion ||
            manifest.CreatedAtUtc == default ||
            manifest.Files is null || manifest.Files.Count is 0 || manifest.Files.Count > MaximumEntryCount)
        {
            throw new InvalidDataException("导入文件不是受支持的 SuperCV 导出包。");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestFile file in manifest.Files)
        {
            if (!IsSafePayloadEntryName(file.Path) ||
                !paths.Add(file.Path) ||
                file.Length < 0 ||
                string.IsNullOrWhiteSpace(file.Sha256) ||
                file.Sha256.Length != 64)
            {
                throw new InvalidDataException("导入文件清单不合法。");
            }

            try
            {
                _ = Convert.FromHexString(file.Sha256);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("导入文件清单的校验值不合法。");
            }
        }
    }

    private static string GetSafePayloadPath(string payloadRoot, string entryName)
    {
        if (!IsSafePayloadEntryName(entryName))
        {
            throw new InvalidDataException("导入文件包含不安全的路径。");
        }

        string relative = entryName[PayloadPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(payloadRoot, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("导入文件包含越界路径。");
        }

        return path;
    }

    private static bool IsSafePayloadEntryName(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(PayloadPrefix, StringComparison.Ordinal) &&
        path.Length > PayloadPrefix.Length &&
        !path.Contains("\\", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal) &&
        path.Split('/').All(segment => segment is not "" and not "." and not "..");

    private static async ValueTask<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private sealed record ArchiveManifest(
        string Format,
        int Version,
        DateTimeOffset CreatedAtUtc,
        IReadOnlyList<ManifestFile> Files);

    private sealed record ManifestFile(string Path, long Length, string Sha256);
}

public sealed record ImportPackageInfo(DateTimeOffset CreatedAtUtc, int FileCount);
