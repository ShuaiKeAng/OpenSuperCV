namespace SuperCV.Infrastructure.Windows;

internal sealed class VersionedJsonRepository<TPayload>
    where TPayload : class
{
    private readonly AtomicJsonFileStore _fileStore;
    private readonly string _path;
    private readonly int _schemaVersion;
    private readonly Func<TPayload, bool>? _payloadValidator;

    internal VersionedJsonRepository(
        AtomicJsonFileStore fileStore,
        string path,
        int schemaVersion,
        Func<TPayload, bool>? payloadValidator = null)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);

        _fileStore = fileStore;
        _path = Path.GetFullPath(path);
        _schemaVersion = schemaVersion;
        _payloadValidator = payloadValidator;
    }

    internal async Task<TPayload?> LoadAsync(CancellationToken cancellationToken = default)
    {
        VersionedJsonDocument<TPayload>? document = await _fileStore.ReadAsync<VersionedJsonDocument<TPayload>>(
                _path,
                IsCurrentDocument,
                cancellationToken)
            .ConfigureAwait(false);

        return document?.Payload;
    }

    internal Task SaveAsync(TPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var document = new VersionedJsonDocument<TPayload>
        {
            SchemaVersion = _schemaVersion,
            Payload = payload,
        };

        return _fileStore.WriteAsync(_path, document, cancellationToken);
    }

    private bool IsCurrentDocument(VersionedJsonDocument<TPayload> document) =>
        document.SchemaVersion == _schemaVersion &&
        document.Payload is not null &&
        (_payloadValidator is null || _payloadValidator(document.Payload));

    private sealed class VersionedJsonDocument<T>
        where T : class
    {
        public int SchemaVersion { get; init; }

        public T? Payload { get; init; }
    }
}
