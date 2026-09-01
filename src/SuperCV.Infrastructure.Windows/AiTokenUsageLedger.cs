using System.Text.Json;
using System.Text.Json.Serialization;
using SuperCV.Domain.AI;
using SuperCV.Domain.Settings;

namespace SuperCV.Infrastructure.Windows;

/// <summary>Durable, local-only ledger of usage values returned by model APIs.</summary>
public sealed class AiTokenUsageLedger
{
    private const int RetainedEntryCount = 5_000;
    private readonly object _gate = new();
    private readonly string? _filePath;
    private readonly AtomicJsonFileStore? _store;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private List<AiTokenUsageEntry> _entries;

    internal AiTokenUsageLedger(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _store = new AtomicJsonFileStore();
        _entries = LoadEntries(_filePath);
    }

    public AiTokenUsageLedger()
    {
        _entries = [];
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AiTokenUsageEntry> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public async Task RecordAsync(
        AiProvider provider,
        string model,
        AiTokenUsage usage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(usage);
        if (!usage.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Add(new AiTokenUsageEntry(DateTimeOffset.UtcNow, provider, model.Trim(), usage));
            if (_entries.Count > RetainedEntryCount)
            {
                _entries.RemoveRange(0, _entries.Count - RetainedEntryCount);
            }
        }

        if (_filePath is not null && _store is not null)
        {
            await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AiTokenUsageEntry[] latestSnapshot;
                lock (_gate)
                {
                    latestSnapshot = _entries.ToArray();
                }

                await _store.WriteAsync(
                        _filePath,
                        new AiTokenUsageDocument { Entries = latestSnapshot },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _persistGate.Release();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<AiTokenUsageEntry> LoadEntries(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            string json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            AiTokenUsageDocument? document = JsonSerializer.Deserialize<AiTokenUsageDocument>(json, options);
            return document?.Entries?
                .Where(entry => entry.Usage is { IsValid: true } && !string.IsNullOrWhiteSpace(entry.Model))
                .TakeLast(RetainedEntryCount)
                .ToList() ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class AiTokenUsageDocument
    {
        public IReadOnlyList<AiTokenUsageEntry>? Entries { get; init; }
    }
}

public sealed record AiTokenUsageEntry(
    DateTimeOffset RecordedAt,
    AiProvider Provider,
    string Model,
    AiTokenUsage Usage);
