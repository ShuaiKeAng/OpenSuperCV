using System.Diagnostics;
using System.IO;

namespace SuperCV;

internal sealed class InstructionDocumentMonitor : IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);
    private const int MaximumAttempts = 4;

    private readonly object _gate = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Func<CancellationToken, Task> _documentChangedAsync;
    private CancellationTokenSource? _pendingChange;
    private bool _disposed;

    internal InstructionDocumentMonitor(
        string documentPath,
        Func<CancellationToken, Task> documentChangedAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        _documentChangedAsync = documentChangedAsync ??
            throw new ArgumentNullException(nameof(documentChangedAsync));

        string fullPath = Path.GetFullPath(documentPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The document must have a parent directory.", nameof(documentPath));
        }

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnDocumentChanged;
        _watcher.Created += OnDocumentChanged;
        _watcher.Renamed += OnDocumentRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        CancellationTokenSource? pendingChange;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pendingChange = _pendingChange;
            _pendingChange = null;
        }

        pendingChange?.Cancel();
        pendingChange?.Dispose();
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnDocumentChanged;
        _watcher.Created -= OnDocumentChanged;
        _watcher.Renamed -= OnDocumentRenamed;
        _watcher.Dispose();
    }

    private void OnDocumentChanged(object sender, FileSystemEventArgs e) => ScheduleValidation();

    private void OnDocumentRenamed(object sender, RenamedEventArgs e) => ScheduleValidation();

    private void ScheduleValidation()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingChange?.Cancel();
            _pendingChange?.Dispose();
            _pendingChange = new CancellationTokenSource();
            token = _pendingChange.Token;
        }

        _ = ValidateAfterSaveAsync(token);
    }

    private async Task ValidateAfterSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounce, cancellationToken).ConfigureAwait(false);
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    await _documentChangedAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException &&
                    attempt < MaximumAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AI instruction document validation failed: {exception}");
        }
    }
}
