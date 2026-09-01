using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SuperCV.Application.Ports;

namespace SuperCV.Infrastructure.Windows;

public sealed class DpapiCredentialStore : ICredentialStore
{
    public const string AiApiKeyName = "ai-api-key";

    private const int FileFormatVersion = 2;
    private const int MaximumCredentialFileBytes = 1024 * 1024;
    private const int MaximumCredentialUtf8Bytes = 64 * 1024;
    private const int TransientReadAttempts = 4;
    private const int HeaderLength = 16;
    private const int ProtectedHeaderLength = 17;
    private const byte ActiveCredentialState = 1;
    private const byte DeletedCredentialState = 2;
    private static readonly TimeSpan TransientReadDelay = TimeSpan.FromMilliseconds(50);
    private static readonly byte[] Magic = "SCV2KEY!"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _credentialPath;

    public DpapiCredentialStore()
        : this(V2Paths.ForCurrentUser())
    {
    }

    public DpapiCredentialStore(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory))
    {
    }

    internal DpapiCredentialStore(V2Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _credentialPath = paths.CredentialFilePath;
    }

    public ValueTask<string?> ReadAsync(
        string name,
        Guid revision,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentialName(name);
        ValidateRevision(revision);
        return ReadCoreAsync(revision, cancellationToken);
    }

    public ValueTask WriteAsync(
        string name,
        Guid revision,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentialName(name);
        ValidateRevision(revision);
        return WriteCoreAsync(revision, secret, isDeleted: false, cancellationToken);
    }

    public ValueTask DeleteAsync(
        string name,
        Guid revision,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentialName(name);
        ValidateRevision(revision);
        return WriteCoreAsync(revision, secret: null, isDeleted: true, cancellationToken);
    }

    private async ValueTask<string?> ReadCoreAsync(
        Guid expectedRevision,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = FileOperationLocks.ForPath(_credentialPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            CredentialReadAttempt primary = await TryReadWithTransientRetryAsync(
                    _credentialPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (primary.Credential?.Revision == expectedRevision)
            {
                return primary.Credential.Secret;
            }

            string backupPath = DurableFileCommitter.GetBackupPath(_credentialPath);
            CredentialReadAttempt backup = await TryReadWithTransientRetryAsync(
                    backupPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (backup.Credential?.Revision == expectedRevision)
            {
                try
                {
                    await DurableFileCommitter.CopyAtomicallyAsync(
                            backupPath,
                            _credentialPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The DPAPI-protected backup remains safe and usable. A later successful
                    // credential write will repair the primary file.
                }

                return backup.Credential.Secret;
            }

            // Never combine settings with a credential from another committed generation.
            // A missing, damaged, or mismatched key safely disables AI until it is re-entered.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask WriteCoreAsync(
        Guid revision,
        string? secret,
        bool isDeleted,
        CancellationToken cancellationToken)
    {
        byte[] plaintext = CreateProtectedPlaintext(revision, secret, isDeleted);

        byte[]? protectedPayload = null;
        byte[]? envelope = null;

        try
        {
            protectedPayload = DpapiCurrentUserProtection.Protect(plaintext);
            envelope = CreateEnvelope(protectedPayload);

            SemaphoreSlim gate = FileOperationLocks.ForPath(_credentialPath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DurableFileCommitter.WriteBytesAtomicallyAsync(
                        _credentialPath,
                        envelope,
                        createBackup: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
    }

    private static void ValidateCredentialName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, AiApiKeyName, StringComparison.Ordinal))
        {
            throw new ArgumentException("This credential store only supports the AI API key.", nameof(name));
        }
    }

    private static void ValidateRevision(Guid revision)
    {
        if (revision == Guid.Empty)
        {
            throw new ArgumentException("A non-empty credential revision is required.", nameof(revision));
        }
    }

    private static byte[] CreateProtectedPlaintext(
        Guid revision,
        string? secret,
        bool isDeleted)
    {
        byte[]? secretBytes = null;
        try
        {
            if (isDeleted)
            {
                if (secret is not null)
                {
                    throw new ArgumentException("A deleted credential cannot contain a secret.", nameof(secret));
                }
            }
            else
            {
                ArgumentException.ThrowIfNullOrEmpty(secret);
                secretBytes = StrictUtf8.GetBytes(secret);
                if (secretBytes.Length > MaximumCredentialUtf8Bytes)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(secret),
                        $"The UTF-8 credential must not exceed {MaximumCredentialUtf8Bytes} bytes.");
                }
            }

            int secretLength = secretBytes?.Length ?? 0;
            byte[] plaintext = GC.AllocateUninitializedArray<byte>(
                ProtectedHeaderLength + secretLength);
            if (!revision.TryWriteBytes(plaintext.AsSpan(0, 16)))
            {
                throw new InvalidOperationException("The credential revision could not be serialized.");
            }

            plaintext[16] = isDeleted ? DeletedCredentialState : ActiveCredentialState;
            secretBytes?.CopyTo(plaintext, ProtectedHeaderLength);
            return plaintext;
        }
        finally
        {
            if (secretBytes is not null)
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
    }

    private static CredentialRecord ParseProtectedPlaintext(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < ProtectedHeaderLength)
        {
            throw new InvalidDataException("The protected credential record is truncated.");
        }

        Guid revision = new(plaintext[..16]);
        if (revision == Guid.Empty)
        {
            throw new InvalidDataException("The protected credential revision is empty.");
        }
        byte state = plaintext[16];
        ReadOnlySpan<byte> secretBytes = plaintext[ProtectedHeaderLength..];

        if (state == DeletedCredentialState)
        {
            if (!secretBytes.IsEmpty)
            {
                throw new InvalidDataException("A credential tombstone contains unexpected data.");
            }

            return new CredentialRecord(revision, Secret: null);
        }

        if (state != ActiveCredentialState ||
            secretBytes.IsEmpty ||
            secretBytes.Length > MaximumCredentialUtf8Bytes)
        {
            throw new InvalidDataException("The protected credential state is invalid.");
        }

        string secret = StrictUtf8.GetString(secretBytes);
        if (secret.Length == 0)
        {
            throw new InvalidDataException("The protected credential is empty.");
        }

        return new CredentialRecord(revision, secret);
    }

    private static byte[] CreateEnvelope(ReadOnlySpan<byte> protectedPayload)
    {
        byte[] envelope = GC.AllocateUninitializedArray<byte>(HeaderLength + protectedPayload.Length);
        Magic.CopyTo(envelope, 0);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(8, 4), FileFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(12, 4), protectedPayload.Length);
        protectedPayload.CopyTo(envelope.AsSpan(HeaderLength));
        return envelope;
    }

    private static byte[] ParseEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderLength || !envelope[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The credential file header is invalid.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(8, 4));
        if (version != FileFormatVersion)
        {
            throw new InvalidDataException("The credential file version is unsupported.");
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(12, 4));
        if (payloadLength <= 0 || payloadLength != envelope.Length - HeaderLength)
        {
            throw new InvalidDataException("The credential file payload length is invalid.");
        }

        return envelope[HeaderLength..].ToArray();
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length <= 0 || stream.Length > MaximumCredentialFileBytes)
        {
            throw new InvalidDataException("The credential file size is invalid.");
        }

        byte[] contents = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(contents, cancellationToken).ConfigureAwait(false);
        return contents;
    }

    private static async Task<CredentialReadAttempt> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[]? envelope = null;
        byte[]? protectedPayload = null;
        byte[]? plaintext = null;

        try
        {
            envelope = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
            protectedPayload = ParseEnvelope(envelope);
            plaintext = DpapiCurrentUserProtection.Unprotect(protectedPayload);

            return CredentialReadAttempt.Success(ParseProtectedPlaintext(plaintext));
        }
        catch (FileNotFoundException)
        {
            return CredentialReadAttempt.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return CredentialReadAttempt.Missing();
        }
        catch (IOException exception) when (FileIoErrorClassifier.IsTransientLock(exception))
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or CryptographicException or DecoderFallbackException)
        {
            return CredentialReadAttempt.Failure(exception);
        }
        finally
        {
            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }

            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static async Task<CredentialReadAttempt> TryReadWithTransientRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= TransientReadAttempts; attempt++)
        {
            try
            {
                return await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (
                FileIoErrorClassifier.IsTransientLock(exception) &&
                attempt < TransientReadAttempts)
            {
                await Task.Delay(TransientReadDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Transient credential read retry terminated unexpectedly.");
    }

    private sealed class CredentialReadAttempt
    {
        private CredentialReadAttempt(CredentialRecord? credential, bool isMissing, Exception? error)
        {
            Credential = credential;
            IsMissing = isMissing;
            Error = error;
        }

        internal CredentialRecord? Credential { get; }

        internal bool IsMissing { get; }

        internal Exception? Error { get; }

        internal static CredentialReadAttempt Success(CredentialRecord credential) =>
            new(credential, isMissing: false, error: null);

        internal static CredentialReadAttempt Missing() =>
            new(credential: null, isMissing: true, error: null);

        internal static CredentialReadAttempt Failure(Exception error) =>
            new(credential: null, isMissing: false, error);
    }

    private sealed record CredentialRecord(Guid Revision, string? Secret);
}

internal sealed class CredentialRecoveryException : IOException
{
    private CredentialRecoveryException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal static CredentialRecoveryException Create(
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

        return new CredentialRecoveryException(
            $"Neither the primary nor backup credential is usable: {Path.GetFileName(path)}.",
            innerException);
    }
}

internal static class DpapiCurrentUserProtection
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] AdditionalEntropy = SHA256.HashData(
        "SuperCV/V2/CurrentUser/AiCredential"u8);

    internal static byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        Transform(plaintext, protect: true);

    internal static byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        Transform(protectedData, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (input.IsEmpty)
        {
            throw new CryptographicException("DPAPI input cannot be empty.");
        }

        IntPtr inputPointer = IntPtr.Zero;
        IntPtr entropyPointer = IntPtr.Zero;
        DataBlob outputBlob = default;

        try
        {
            inputPointer = Marshal.AllocHGlobal(input.Length);
            byte[] inputCopy = input.ToArray();
            try
            {
                Marshal.Copy(inputCopy, 0, inputPointer, inputCopy.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inputCopy);
            }

            entropyPointer = Marshal.AllocHGlobal(AdditionalEntropy.Length);
            Marshal.Copy(AdditionalEntropy, 0, entropyPointer, AdditionalEntropy.Length);

            var inputBlob = new DataBlob(input.Length, inputPointer);
            var entropyBlob = new DataBlob(AdditionalEntropy.Length, entropyPointer);

            bool succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "SuperCV V2 AI credential",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);

            if (!succeeded)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new CryptographicException(
                    "Windows DPAPI could not process the current-user credential.",
                    new Win32Exception(errorCode));
            }

            if (outputBlob.Data == IntPtr.Zero || outputBlob.Length <= 0)
            {
                throw new CryptographicException("Windows DPAPI returned an empty result.");
            }

            byte[] result = GC.AllocateUninitializedArray<byte>(outputBlob.Length);
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Length);
            return result;
        }
        finally
        {
            ZeroAndFree(inputPointer, input.Length);
            ZeroAndFree(entropyPointer, AdditionalEntropy.Length);

            if (outputBlob.Data != IntPtr.Zero)
            {
                _ = LocalFree(outputBlob.Data);
            }
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (int index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        Marshal.FreeHGlobal(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob
    {
        internal DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }

        internal int Length { get; }

        internal IntPtr Data { get; }
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
