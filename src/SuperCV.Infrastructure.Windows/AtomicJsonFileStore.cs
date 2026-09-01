using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCV.Infrastructure.Windows;

internal sealed class AtomicJsonFileStore
{
    private const int BufferSize = 64 * 1024;
    private const int TransientReadAttempts = 4;
    private static readonly TimeSpan TransientReadDelay = TimeSpan.FromMilliseconds(50);
    private readonly JsonSerializerOptions _serializerOptions;

    internal AtomicJsonFileStore(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions is null
            ? CreateDefaultOptions()
            : new JsonSerializerOptions(serializerOptions);

        _serializerOptions.MakeReadOnly(populateMissingResolver: true);
    }

    internal JsonSerializerOptions SerializerOptions => _serializerOptions;

    internal async Task<T?> ReadAsync<T>(
        string path,
        Func<T, bool>? validator = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        string fullPath = Path.GetFullPath(path);
        SemaphoreSlim gate = FileOperationLocks.ForPath(fullPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ReadAttempt<T> primary = await TryReadWithTransientRetryAsync(
                    fullPath,
                    validator,
                    cancellationToken)
                .ConfigureAwait(false);
            if (primary.Value is not null)
            {
                return primary.Value;
            }

            string backupPath = DurableFileCommitter.GetBackupPath(fullPath);
            ReadAttempt<T> backup = await TryReadWithTransientRetryAsync(
                    backupPath,
                    validator,
                    cancellationToken)
                .ConfigureAwait(false);
            if (backup.Value is not null)
            {
                try
                {
                    await DurableFileCommitter.CopyAtomicallyAsync(backupPath, fullPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A valid backup remains usable even when a read-only or transiently locked
                    // directory prevents immediate repair. The next successful save repairs it.
                }

                return backup.Value;
            }

            if (primary.IsMissing && backup.IsMissing)
            {
                return null;
            }

            throw JsonFileRecoveryException.Create(fullPath, primary.Error, backup.Error);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task WriteAsync<T>(
        string path,
        T document,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        string fullPath = Path.GetFullPath(path);
        SemaphoreSlim gate = FileOperationLocks.ForPath(fullPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DurableFileCommitter.WriteAtomicallyAsync(
                    fullPath,
                    async (stream, token) =>
                    {
                        await JsonSerializer.SerializeAsync(stream, document, _serializerOptions, token)
                            .ConfigureAwait(false);
                    },
                    createBackup: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task DeleteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        SemaphoreSlim gate = FileOperationLocks.ForPath(fullPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            DeleteIfPresent(fullPath);
            DeleteIfPresent(DurableFileCommitter.GetBackupPath(fullPath));
        }
        finally
        {
            gate.Release();
        }
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // A document that never existed (for example, an empty workspace history)
            // is already in the requested deleted state.
        }
    }

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private async Task<ReadAttempt<T>> TryReadAsync<T>(
        string path,
        Func<T, bool>? validator,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            T? value = await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidDataException("The JSON document contained a null root value.");
            }

            if (validator is not null && !validator(value))
            {
                throw new InvalidDataException("The JSON document failed schema validation.");
            }

            return ReadAttempt<T>.Success(value);
        }
        catch (FileNotFoundException)
        {
            return ReadAttempt<T>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ReadAttempt<T>.Missing();
        }
        catch (IOException exception) when (FileIoErrorClassifier.IsTransientLock(exception))
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            InvalidDataException or
            IOException or
            ArgumentException)
        {
            return ReadAttempt<T>.Failure(exception);
        }
    }

    private async Task<ReadAttempt<T>> TryReadWithTransientRetryAsync<T>(
        string path,
        Func<T, bool>? validator,
        CancellationToken cancellationToken)
        where T : class
    {
        for (int attempt = 1; attempt <= TransientReadAttempts; attempt++)
        {
            try
            {
                return await TryReadAsync(path, validator, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (
                FileIoErrorClassifier.IsTransientLock(exception) &&
                attempt < TransientReadAttempts)
            {
                await Task.Delay(TransientReadDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Transient JSON read retry terminated unexpectedly.");
    }

    private sealed record ReadAttempt<T>(T? Value, bool IsMissing, Exception? Error)
        where T : class
    {
        internal static ReadAttempt<T> Success(T value) => new(value, IsMissing: false, Error: null);

        internal static ReadAttempt<T> Missing() => new(Value: null, IsMissing: true, Error: null);

        internal static ReadAttempt<T> Failure(Exception error) => new(Value: null, IsMissing: false, error);
    }
}

internal sealed class JsonFileRecoveryException : IOException
{
    private JsonFileRecoveryException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal static JsonFileRecoveryException Create(
        string path,
        Exception? primaryError,
        Exception? backupError)
    {
        Exception? innerException = (primaryError, backupError) switch
        {
            (not null, not null) => new AggregateException(primaryError, backupError),
            (not null, null) => primaryError,
            (null, not null) => backupError,
            _ => null,
        };

        return new JsonFileRecoveryException(
            $"Neither the primary nor backup JSON document is usable: {Path.GetFileName(path)}.",
            innerException);
    }
}
