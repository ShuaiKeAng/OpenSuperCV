using System.Security.Cryptography;
using System.Text;

namespace SuperCV.Infrastructure.Windows;

/// <summary>
/// Holds a kernel semaphore for one V2 data root so two processes cannot overwrite each
/// other's whole-file snapshots. The scope is local to the interactive Windows session.
/// </summary>
public sealed class WindowsSingleInstanceLease : IDisposable
{
    private readonly object _listenerGate = new();
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Semaphore? _semaphore;
    private Task? _listenerTask;

    private WindowsSingleInstanceLease(
        Semaphore semaphore,
        EventWaitHandle activationEvent)
    {
        _semaphore = semaphore;
        _activationEvent = activationEvent;
    }

    public static WindowsSingleInstanceLease? TryAcquire(string dataRoot)
    {
        string scopeHash = GetScopeHash(dataRoot);
        string semaphoreName = $@"Local\SuperCV.V2.{scopeHash}";
        string activationEventName = $@"Local\SuperCV.V2.Activate.{scopeHash}";

        var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            activationEventName);
        var semaphore = new Semaphore(1, 1, semaphoreName);
        try
        {
            if (!semaphore.WaitOne(0))
            {
                semaphore.Dispose();
                activationEvent.Dispose();
                return null;
            }

            return new WindowsSingleInstanceLease(semaphore, activationEvent);
        }
        catch
        {
            semaphore.Dispose();
            activationEvent.Dispose();
            throw;
        }
    }

    public static void SignalActivationRequest(string dataRoot)
    {
        string scopeHash = GetScopeHash(dataRoot);
        string activationEventName = $@"Local\SuperCV.V2.Activate.{scopeHash}";
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            activationEventName);
        activationEvent.Set();
    }

    public void StartActivationListener(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);

        lock (_listenerGate)
        {
            ObjectDisposedException.ThrowIf(_semaphore is null, this);
            if (_listenerTask is not null)
            {
                throw new InvalidOperationException("The activation listener has already started.");
            }

            _listenerTask = Task.Run(
                () => ListenForActivationRequests(activationRequested),
                CancellationToken.None);
        }
    }

    public void Dispose()
    {
        Semaphore? semaphore = Interlocked.Exchange(ref _semaphore, null);
        if (semaphore is null)
        {
            return;
        }

        try
        {
            _listenerCancellation.Cancel();
            Task? listenerTask;
            lock (_listenerGate)
            {
                listenerTask = _listenerTask;
            }

            listenerTask?.GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                _listenerCancellation.Dispose();
                _activationEvent.Dispose();
            }
            finally
            {
                semaphore.Release();
                semaphore.Dispose();
            }
        }
    }

    private static string GetScopeHash(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
    }

    private void ListenForActivationRequests(Action activationRequested)
    {
        WaitHandle[] waitHandles =
        [
            _activationEvent,
            _listenerCancellation.Token.WaitHandle,
        ];

        while (WaitHandle.WaitAny(waitHandles) == 0)
        {
            activationRequested();
        }
    }
}
