using SuperCV.Application.Ports;
using SuperCV.Domain.Settings;

namespace SuperCV.Application.Settings;

public sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AppSettings Settings { get; }
}

public sealed class SettingsService : IAsyncDisposable
{
    public const string AiApiKeyCredentialName = "ai-api-key";

    private readonly object _gate = new();
    private readonly ISettingsRepository _repository;
    private readonly ICredentialStore _credentialStore;
    private readonly CoalescingSaveQueue<AppSettings> _saveQueue;
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);
    private AppSettings _settings = new();
    private string _apiKey = string.Empty;
    private bool _initialized;
    private bool _stopping;
    private bool _disposed;

    public SettingsService(
        ISettingsRepository repository,
        ICredentialStore credentialStore)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _saveQueue = new CoalescingSaveQueue<AppSettings>(
            CreatePersistenceSnapshot,
            (snapshot, token) => _repository.SaveAsync(snapshot, token),
            TimeSpan.FromMilliseconds(300));
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public AppSettings Snapshot
    {
        get
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                return _settings;
            }
        }
    }

    public string ApiKey
    {
        get
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                return _apiKey;
            }
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        AppSettings? loadedSettings = await _repository.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        AppSettings normalized = (loadedSettings ?? new AppSettings()).Normalize();
        string? loadedKey = await _credentialStore
            .ReadAsync(
                AiApiKeyCredentialName,
                normalized.CredentialRevision,
                cancellationToken)
            .ConfigureAwait(false);

        bool needsSave = loadedSettings is null || normalized != loadedSettings;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            _settings = normalized;
            _apiKey = loadedKey ?? string.Empty;
            _initialized = true;
        }

        if (needsSave)
        {
            _saveQueue.RequestSave();
        }

        RaiseChanged(normalized);
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppSettings? updated = null;
        _settingsUpdateGate.Wait();
        try
        {
            lock (_gate)
            {
                EnsureReadyLocked();
                AppSettings candidate = (update(_settings) ?? throw new InvalidOperationException(
                    "A settings update cannot return null.")).Normalize() with
                {
                    CredentialRevision = _settings.CredentialRevision,
                };
                if (candidate == _settings)
                {
                    return;
                }

                _settings = candidate;
                updated = candidate;
            }

            _saveQueue.RequestSave();
        }
        finally
        {
            _settingsUpdateGate.Release();
        }

        RaiseChanged(updated);
    }

    public ValueTask UpdateAndPersistAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default) =>
        PersistAsync(update, apiKey: null, updateApiKey: false, cancellationToken);

    public ValueTask UpdateSettingsAndApiKeyAsync(
        Func<AppSettings, AppSettings> update,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return PersistAsync(update, apiKey, updateApiKey: true, cancellationToken);
    }

    public ValueTask SetApiKeyAsync(
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        PersistAsync(
            static settings => settings,
            apiKey,
            updateApiKey: true,
            cancellationToken);

    public ValueTask ResetToDefaultsAsync(
        CancellationToken cancellationToken = default) =>
        PersistAsync(
            static current => new AppSettings
            {
                InstructionPresetVersion = current.InstructionPresetVersion,
            },
            apiKey: string.Empty,
            updateApiKey: true,
            cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _saveQueue.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _settingsUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _stopping = true;
            }

            try
            {
                await _saveQueue.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _disposed = true;
                    _apiKey = string.Empty;
                }
            }
        }
        finally
        {
            _settingsUpdateGate.Release();
        }
    }

    private async ValueTask PersistAsync(
        Func<AppSettings, AppSettings> update,
        string? apiKey,
        bool updateApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppSettings? updated = null;
        string normalizedApiKey = apiKey?.Trim() ?? string.Empty;

        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings current;
            AppSettings candidate;
            string currentApiKey;
            lock (_gate)
            {
                EnsureReadyLocked();
                current = _settings;
                currentApiKey = _apiKey;
                candidate = (update(current) ?? throw new InvalidOperationException(
                    "A settings update cannot return null.")).Normalize() with
                {
                    // The revision is an internal consistency token. Callers cannot mutate it;
                    // it advances exactly when the protected secret changes.
                    CredentialRevision = current.CredentialRevision,
                };
            }

            bool apiKeyChanged = updateApiKey && !string.Equals(
                currentApiKey,
                normalizedApiKey,
                StringComparison.Ordinal);
            if (apiKeyChanged)
            {
                candidate = candidate with { CredentialRevision = Guid.NewGuid() };
            }

            // Drain earlier coalesced changes first. While this gate is held no later update can
            // overtake the durable transaction or overwrite it from the background queue.
            await _saveQueue.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (candidate == current && !apiKeyChanged)
            {
                return;
            }

            bool credentialCommitted = false;
            try
            {
                if (apiKeyChanged)
                {
                    await PersistCredentialAsync(
                            candidate.CredentialRevision,
                            normalizedApiKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                    credentialCommitted = true;
                }

                await _repository.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception persistenceException)
            {
                if (credentialCommitted)
                {
                    try
                    {
                        await PersistCredentialAsync(
                                current.CredentialRevision,
                                currentApiKey,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "Settings persistence failed and the previous credential revision could not be restored.",
                            persistenceException,
                            rollbackException);
                    }
                }

                throw;
            }

            lock (_gate)
            {
                EnsureReadyLocked();
                _settings = candidate;
                if (apiKeyChanged)
                {
                    _apiKey = normalizedApiKey;
                }

                updated = candidate;
            }
        }
        finally
        {
            _settingsUpdateGate.Release();
        }

        RaiseChanged(updated);
    }

    private ValueTask PersistCredentialAsync(
        Guid revision,
        string apiKey,
        CancellationToken cancellationToken) =>
        apiKey.Length == 0
            ? _credentialStore.DeleteAsync(
                AiApiKeyCredentialName,
                revision,
                cancellationToken)
            : _credentialStore.WriteAsync(
                AiApiKeyCredentialName,
                revision,
                apiKey,
                cancellationToken);

    private AppSettings CreatePersistenceSnapshot()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    private void RaiseChanged(AppSettings? settings)
    {
        if (settings is null || Changed is not { } handlers)
        {
            return;
        }

        var args = new SettingsChangedEventArgs(settings);
        foreach (EventHandler<SettingsChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"A settings change observer failed: {exception.Message}");
            }
        }
    }

    private void EnsureReadyLocked()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Settings service has not been initialized.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
}
