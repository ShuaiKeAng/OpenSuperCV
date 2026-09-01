using System.Collections.Concurrent;

namespace SuperCV.Infrastructure.Windows;

internal static class FileOperationLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static SemaphoreSlim ForPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return Gates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
    }
}

internal static class FileIoErrorClassifier
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorLockFailed = 167;

    internal static bool IsTransientLock(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int win32Error = exception.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation or ErrorLockFailed;
    }
}

internal static class DurableFileCommitter
{
    private const int BufferSize = 64 * 1024;

    internal static string GetBackupPath(string destinationPath) =>
        Path.GetFullPath(destinationPath) + ".bak";

    internal static async Task WriteBytesAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> contents,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        await WriteAtomicallyAsync(
                destinationPath,
                async (stream, token) =>
                {
                    await stream.WriteAsync(contents, token).ConfigureAwait(false);
                },
                createBackup,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);

        await WriteAtomicallyAsync(
                destinationPath,
                async (destination, token) =>
                {
                    await using var source = new FileStream(
                        fullSourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    await source.CopyToAsync(destination, BufferSize, token).ConfigureAwait(false);
                },
                createBackup: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task WriteAtomicallyAsync(
        string destinationPath,
        Func<FileStream, CancellationToken, Task> writeAsync,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullDestinationPath))
            {
                string? backupPath = createBackup ? GetBackupPath(fullDestinationPath) : null;
                if (backupPath is not null && File.Exists(backupPath))
                {
                    // The destination is still a valid copy at this point. Removing the older
                    // backup lets File.Replace create a fresh backup without risking the primary.
                    File.Delete(backupPath);
                }

                File.Replace(temporaryPath, fullDestinationPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                // The temporary file is deliberately in the destination directory, so this
                // rename stays on one volume and is atomic.
                File.Move(temporaryPath, fullDestinationPath);
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
