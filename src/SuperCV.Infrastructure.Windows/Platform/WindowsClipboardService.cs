using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SuperCV.Application.Ports;
using SuperCV.Domain.Clipboard;

namespace SuperCV.Infrastructure.Windows.Platform;

public sealed class WindowsClipboardService : IClipboardService
{
    private const int OpenAttemptCount = 12;
    private const int SynchronousOpenAttemptCount = 5;
    private const int InitialOpenDelayMilliseconds = 2;
    private const int MaximumOpenDelayMilliseconds = 32;
    private const int MessageThreadGateWaitMilliseconds = 25;

    private static readonly long SelfSuppressionTicks = Stopwatch.Frequency * 2L;
    private static readonly int[] NonPasteModifierVirtualKeys =
    [
        NativeMethods.VkLWin,
        NativeMethods.VkRWin,
        NativeMethods.VkLMenu,
        NativeMethods.VkRMenu,
        NativeMethods.VkMenu,
        NativeMethods.VkLShift,
        NativeMethods.VkRShift,
        NativeMethods.VkShift,
    ];

    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ClipboardMessageWindow _messageWindow;
    private readonly ConcurrentQueue<ClipboardSnapshot> _eventQueue = new();
    private readonly ClipboardSelfChangeTracker _selfChangeTracker =
        new(SelfSuppressionTicks);
    private readonly WindowsClipboardImageCache _imageCache;
    private readonly uint _htmlFormat;
    private readonly uint _rtfFormat;
    private readonly uint _pngFormat;

    private uint _lastObservedSequence;
    private int _eventDispatcherActive;
    private int _disposed;

    public WindowsClipboardService(string imageCacheDirectory)
    {
        _imageCache = new WindowsClipboardImageCache(imageCacheDirectory);
        _htmlFormat = RegisterClipboardFormat("HTML Format");
        _rtfFormat = RegisterClipboardFormat("Rich Text Format");
        _pngFormat = RegisterClipboardFormat("PNG");
        _messageWindow = new ClipboardMessageWindow(HandleClipboardUpdate);
    }

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public bool IsMonitoring =>
        Volatile.Read(ref _disposed) == 0 && _messageWindow.IsMonitoring;

    public bool ImageCaptureEnabled
    {
        get => Volatile.Read(ref _imageCaptureEnabled) != 0;
        set => Volatile.Write(ref _imageCaptureEnabled, value ? 1 : 0);
    }

    private int _imageCaptureEnabled;

    public async ValueTask StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _messageWindow.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await _messageWindow.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<ClipboardSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        nint owner = await _messageWindow
            .GetWindowHandleAsync(cancellationToken)
            .ConfigureAwait(false);

        await _clipboardGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await OpenClipboardAsync(owner, cancellationToken).ConfigureAwait(false);

            Exception? operationException = null;
            ClipboardCapture capture;
            try
            {
                capture = CaptureWhileOpen();
            }
            catch (Exception exception)
            {
                operationException = exception;
                throw;
            }
            finally
            {
                CloseClipboard(operationException);
            }

            return FinalizeCapture(capture);
        }
        finally
        {
            _clipboardGate.Release();
        }
    }

    public async ValueTask WriteAsync(
        ClipboardPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ThrowIfDisposed();

        List<ClipboardWriteBuffer> buffers = PrepareWriteBuffers(payload);
        try
        {
            nint owner = await _messageWindow
                .GetWindowHandleAsync(cancellationToken)
                .ConfigureAwait(false);

            await _clipboardGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await OpenClipboardAsync(owner, cancellationToken).ConfigureAwait(false);

                Exception? operationException = null;
                bool clipboardMutated = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!NativeMethods.EmptyClipboard())
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Unable to empty the clipboard.");
                    }

                    clipboardMutated = true;
                    foreach (ClipboardWriteBuffer buffer in buffers)
                    {
                        nint result = NativeMethods.SetClipboardData(
                            buffer.NativeFormat,
                            buffer.Memory.Handle);
                        if (result == nint.Zero)
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                $"Unable to set clipboard format {buffer.NativeFormat}.");
                        }

                        _ = buffer.Memory.TransferOwnership();
                    }
                }
                catch (Exception exception)
                {
                    operationException = exception;
                    throw;
                }
                finally
                {
                    if (clipboardMutated)
                    {
                        MarkCurrentSequenceAsSelfChange(
                            operationException is null ? payload : null);
                    }

                    CloseClipboard(operationException);
                }
            }
            finally
            {
                _clipboardGate.Release();
            }
        }
        finally
        {
            foreach (ClipboardWriteBuffer buffer in buffers)
            {
                buffer.Dispose();
            }
        }
    }

    public ValueTask PasteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        PasteInputPlan plan = CreatePasteInputPlan(CapturePressedKeys());

        uint inserted = NativeMethods.SendInput(
            checked((uint)plan.Inputs.Length),
            plan.Inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (inserted != plan.Inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            SendKeyReleaseRecovery(plan);
            throw new Win32Exception(error, "Unable to synthesize the paste shortcut.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteCachedImagesExceptAsync(
        IReadOnlySet<string> retainedImageLinks,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _imageCache.DeleteExceptAsync(retainedImageLinks, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _clipboardGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _messageWindow.DisposeAsync().ConfigureAwait(false);
                ClipboardChanged = null;
                while (_eventQueue.TryDequeue(out _))
                {
                }
            }
            finally
            {
                _clipboardGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static uint RegisterClipboardFormat(string name)
    {
        uint format = NativeMethods.RegisterClipboardFormat(name);
        if (format == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to register the clipboard format '{name}'.");
        }

        return format;
    }

    private static bool IsKeyPressed(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static HashSet<int> CapturePressedKeys()
    {
        var pressedKeys = new HashSet<int>();
        CaptureModifier(
            pressedKeys,
            NativeMethods.VkShift,
            NativeMethods.VkLShift,
            NativeMethods.VkRShift);
        CaptureModifier(
            pressedKeys,
            NativeMethods.VkControl,
            NativeMethods.VkLControl,
            NativeMethods.VkRControl);
        CaptureModifier(
            pressedKeys,
            NativeMethods.VkMenu,
            NativeMethods.VkLMenu,
            NativeMethods.VkRMenu);

        if (IsKeyPressed(NativeMethods.VkLWin))
        {
            pressedKeys.Add(NativeMethods.VkLWin);
        }

        if (IsKeyPressed(NativeMethods.VkRWin))
        {
            pressedKeys.Add(NativeMethods.VkRWin);
        }

        return pressedKeys;
    }

    private static void CaptureModifier(
        ISet<int> pressedKeys,
        int genericVirtualKey,
        int leftVirtualKey,
        int rightVirtualKey)
    {
        bool sideCaptured = false;
        if (IsKeyPressed(leftVirtualKey))
        {
            pressedKeys.Add(leftVirtualKey);
            sideCaptured = true;
        }

        if (IsKeyPressed(rightVirtualKey))
        {
            pressedKeys.Add(rightVirtualKey);
            sideCaptured = true;
        }

        if (!sideCaptured && IsKeyPressed(genericVirtualKey))
        {
            pressedKeys.Add(genericVirtualKey);
        }
    }

    internal static PasteInputPlan CreatePasteInputPlan(IReadOnlySet<int> pressedKeys)
    {
        ArgumentNullException.ThrowIfNull(pressedKeys);

        bool controlAlreadyPressed =
            pressedKeys.Contains(NativeMethods.VkControl) ||
            pressedKeys.Contains(NativeMethods.VkLControl) ||
            pressedKeys.Contains(NativeMethods.VkRControl);
        int[] temporarilyReleasedModifiers = NonPasteModifierVirtualKeys
            .Where(pressedKeys.Contains)
            .ToArray();
        var inputs = new List<NativeMethods.Input>(
            temporarilyReleasedModifiers.Length * 2 +
            (controlAlreadyPressed ? 2 : 4));

        for (int index = temporarilyReleasedModifiers.Length - 1; index >= 0; index--)
        {
            inputs.Add(CreateKeyboardInput(
                checked((ushort)temporarilyReleasedModifiers[index]),
                keyUp: true));
        }

        if (!controlAlreadyPressed)
        {
            inputs.Add(CreateKeyboardInput(
                checked((ushort)NativeMethods.VkControl),
                keyUp: false));
        }

        inputs.Add(CreateKeyboardInput(NativeMethods.VkV, keyUp: false));
        inputs.Add(CreateKeyboardInput(NativeMethods.VkV, keyUp: true));

        if (!controlAlreadyPressed)
        {
            inputs.Add(CreateKeyboardInput(
                checked((ushort)NativeMethods.VkControl),
                keyUp: true));
        }

        foreach (int virtualKey in temporarilyReleasedModifiers)
        {
            inputs.Add(CreateKeyboardInput(checked((ushort)virtualKey), keyUp: false));
        }

        return new PasteInputPlan(
            inputs.ToArray(),
            controlAlreadyPressed,
            temporarilyReleasedModifiers);
    }

    private static NativeMethods.Input CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        uint flags = keyUp ? NativeMethods.KeyEventKeyUp : 0;
        if (virtualKey is
            NativeMethods.VkRControl or
            NativeMethods.VkRMenu or
            NativeMethods.VkLWin or
            NativeMethods.VkRWin)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = flags,
                },
            },
        };
    }

    private static void SendKeyReleaseRecovery(PasteInputPlan plan)
    {
        var recovery = new List<NativeMethods.Input>(
            plan.TemporarilyReleasedModifiers.Length + 2)
        {
            CreateKeyboardInput(NativeMethods.VkV, keyUp: true),
        };
        if (!plan.ControlAlreadyPressed)
        {
            recovery.Add(CreateKeyboardInput(
                checked((ushort)NativeMethods.VkControl),
                keyUp: true));
        }

        foreach (int virtualKey in plan.TemporarilyReleasedModifiers)
        {
            recovery.Add(CreateKeyboardInput(checked((ushort)virtualKey), keyUp: false));
        }

        NativeMethods.Input[] recoveryInputs = recovery.ToArray();
        _ = NativeMethods.SendInput(
            checked((uint)recoveryInputs.Length),
            recoveryInputs,
            Marshal.SizeOf<NativeMethods.Input>());
    }

    internal readonly record struct PasteInputPlan(
        NativeMethods.Input[] Inputs,
        bool ControlAlreadyPressed,
        int[] TemporarilyReleasedModifiers);

    private static void CloseClipboard(Exception? operationException)
    {
        if (NativeMethods.CloseClipboard())
        {
            return;
        }

        var closeException = new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Unable to close the clipboard.");
        if (operationException is null)
        {
            throw closeException;
        }

        Trace.TraceError("Closing the clipboard failed after another error: {0}", closeException);
    }

    private async ValueTask OpenClipboardAsync(
        nint owner,
        CancellationToken cancellationToken)
    {
        int delay = InitialOpenDelayMilliseconds;
        int lastError = 0;
        for (int attempt = 0; attempt < OpenAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.OpenClipboard(owner))
            {
                return;
            }

            lastError = Marshal.GetLastWin32Error();
            if (attempt + 1 < OpenAttemptCount)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, MaximumOpenDelayMilliseconds);
            }
        }

        throw new Win32Exception(lastError, "The clipboard is busy and could not be opened.");
    }

    private static bool TryOpenClipboardSynchronously(nint owner, out int lastError)
    {
        int delay = InitialOpenDelayMilliseconds;
        lastError = 0;
        for (int attempt = 0; attempt < SynchronousOpenAttemptCount; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
            {
                return true;
            }

            lastError = Marshal.GetLastWin32Error();
            if (attempt + 1 < SynchronousOpenAttemptCount)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, MaximumOpenDelayMilliseconds);
            }
        }

        return false;
    }

    private ClipboardCapture CaptureWhileOpen()
    {
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        var formats = new Dictionary<ClipboardFormat, string>();

        ReadFormatWhileOpen(formats, ClipboardFormat.UnicodeText, NativeMethods.CfUnicodeText);
        ReadFormatWhileOpen(formats, ClipboardFormat.Text, NativeMethods.CfText);
        ReadFormatWhileOpen(formats, ClipboardFormat.Html, _htmlFormat);
        ReadFormatWhileOpen(formats, ClipboardFormat.Rtf, _rtfFormat);

        RawClipboardImage? image = ImageCaptureEnabled
            ? ReadImageWhileOpen()
            : null;
        return new ClipboardCapture(sequence, formats, image);
    }

    private ClipboardSnapshot FinalizeCapture(ClipboardCapture capture)
    {
        ClipboardPayload payload;
        bool preferImage = capture.Image is not null &&
                           ClipboardCapturePolicy.ShouldPreferImage(capture.Formats);
        if (capture.Formats.Count > 0 && !preferImage)
        {
            payload = new ClipboardPayload(capture.Formats);
        }
        else if (capture.Image is not null)
        {
            string? imageLink;
            if (capture.Image.FilePath is not null)
            {
                imageLink = _imageCache.TrySaveFile(
                    capture.Image.FilePath,
                    out string cachedImageLink)
                    ? cachedImageLink
                    : null;
            }
            else
            {
                imageLink = _imageCache.Save(
                    capture.Image.Bytes,
                    capture.Image.IsPng);
            }

            payload = imageLink is null
                ? ClipboardPayload.Empty
                : new ClipboardPayload(
                    new Dictionary<ClipboardFormat, string>
                    {
                        [ClipboardFormat.Image] = imageLink,
                    });
        }
        else
        {
            payload = ClipboardPayload.Empty;
        }

        return new ClipboardSnapshot(
            payload,
            capture.Sequence,
            _selfChangeTracker.IsSelfChange(
                capture.Sequence,
                payload,
                Stopwatch.GetTimestamp()),
            DateTimeOffset.UtcNow);
    }

    private RawClipboardImage? ReadImageWhileOpen()
    {
        if (TryReadBinaryFormatWhileOpen(_pngFormat, out byte[] png))
        {
            return new RawClipboardImage(png, IsPng: true);
        }

        if (TryReadBinaryFormatWhileOpen(NativeMethods.CfDibV5, out byte[] dibV5))
        {
            return new RawClipboardImage(dibV5, IsPng: false);
        }

        if (TryReadBinaryFormatWhileOpen(NativeMethods.CfDib, out byte[] dib))
        {
            return new RawClipboardImage(dib, IsPng: false);
        }

        return ReadImageFileDropWhileOpen();
    }

    private static RawClipboardImage? ReadImageFileDropWhileOpen()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfHDrop))
        {
            return null;
        }

        nint dropHandle = NativeMethods.GetClipboardData(NativeMethods.CfHDrop);
        if (dropHandle == nint.Zero ||
            NativeMethods.DragQueryFile(dropHandle, uint.MaxValue, null, 0) != 1)
        {
            return null;
        }

        uint pathLength = NativeMethods.DragQueryFile(dropHandle, 0, null, 0);
        if (pathLength == 0 || pathLength > 32_767)
        {
            return null;
        }

        var path = new StringBuilder(checked((int)pathLength + 1));
        return NativeMethods.DragQueryFile(
                dropHandle,
                0,
                path,
                checked((uint)path.Capacity)) > 0
            ? new RawClipboardImage([], IsPng: false, path.ToString())
            : null;
    }

    private static bool TryReadBinaryFormatWhileOpen(uint nativeFormat, out byte[] bytes)
    {
        bytes = [];
        if (!NativeMethods.IsClipboardFormatAvailable(nativeFormat))
        {
            return false;
        }

        nint handle = NativeMethods.GetClipboardData(nativeFormat);
        if (handle == nint.Zero)
        {
            return false;
        }

        bytes = ClipboardMemory.ReadBytes(
            handle,
            WindowsClipboardImageCache.MaximumImageBytes);
        return bytes.Length > 0;
    }

    private void ReadFormatWhileOpen(
        IDictionary<ClipboardFormat, string> destination,
        ClipboardFormat domainFormat,
        uint nativeFormat)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(nativeFormat))
        {
            return;
        }

        nint handle = NativeMethods.GetClipboardData(nativeFormat);
        if (handle == nint.Zero)
        {
            return;
        }

        string text = ClipboardMemory.ReadText(
            handle,
            nativeFormat,
            _htmlFormat,
            _rtfFormat);
        if (!string.IsNullOrEmpty(text))
        {
            destination[domainFormat] = text;
        }
    }

    private List<ClipboardWriteBuffer> PrepareWriteBuffers(ClipboardPayload payload)
    {
        var buffers = new List<ClipboardWriteBuffer>(payload.IsImage ? 2 : payload.Formats.Count);
        try
        {
            if (payload.IsImage)
            {
                (byte[] png, byte[] dib) = _imageCache.ReadClipboardFormats(payload.ImageLink);
                buffers.Add(new ClipboardWriteBuffer(
                    _pngFormat,
                    ClipboardMemory.AllocateBytes(
                        png,
                        WindowsClipboardImageCache.MaximumImageBytes)));
                buffers.Add(new ClipboardWriteBuffer(
                    NativeMethods.CfDib,
                    ClipboardMemory.AllocateBytes(
                        dib,
                        WindowsClipboardImageCache.MaximumImageBytes)));
                return buffers;
            }

            foreach ((ClipboardFormat format, string text) in payload.Formats)
            {
                uint nativeFormat = ToNativeFormat(format);
                buffers.Add(new ClipboardWriteBuffer(
                    nativeFormat,
                    ClipboardMemory.AllocateText(
                        text,
                        nativeFormat,
                        _htmlFormat,
                        _rtfFormat)));
            }

            return buffers;
        }
        catch
        {
            foreach (ClipboardWriteBuffer buffer in buffers)
            {
                buffer.Dispose();
            }

            throw;
        }
    }

    private uint ToNativeFormat(ClipboardFormat format)
    {
        return format switch
        {
            ClipboardFormat.Text => NativeMethods.CfText,
            ClipboardFormat.UnicodeText => NativeMethods.CfUnicodeText,
            ClipboardFormat.Html => _htmlFormat,
            ClipboardFormat.Rtf => _rtfFormat,
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Unsupported clipboard format."),
        };
    }

    private sealed record ClipboardCapture(
        uint Sequence,
        Dictionary<ClipboardFormat, string> Formats,
        RawClipboardImage? Image);

    private sealed record RawClipboardImage(
        byte[] Bytes,
        bool IsPng,
        string? FilePath = null);

    private void MarkCurrentSequenceAsSelfChange(ClipboardPayload? completedPayload)
    {
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        _selfChangeTracker.RecordWrite(
            sequence,
            completedPayload,
            Stopwatch.GetTimestamp());
    }

    private void HandleClipboardUpdate()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_messageWindow.IsMonitoring)
        {
            return;
        }

        if (!_clipboardGate.Wait(MessageThreadGateWaitMilliseconds))
        {
            return;
        }

        try
        {
            nint owner = _messageWindow.WindowHandle;
            int lastError = 0;
            if (owner == nint.Zero ||
                !TryOpenClipboardSynchronously(owner, out lastError))
            {
                if (lastError != 0)
                {
                    Trace.TraceInformation(
                        "Clipboard update ignored because the producer was still busy (error {0}).",
                        lastError);
                }

                return;
            }

            Exception? operationException = null;
            ClipboardCapture? capture = null;
            try
            {
                capture = CaptureWhileOpen();
            }
            catch (Exception exception)
            {
                operationException = exception;
                Trace.TraceError("Clipboard update capture failed: {0}", exception);
            }
            finally
            {
                try
                {
                    CloseClipboard(operationException);
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Clipboard update close failed: {0}", exception);
                }
            }

            if (operationException is null && capture is not null)
            {
                try
                {
                    PublishSnapshot(FinalizeCapture(capture));
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Clipboard image finalization failed: {0}", exception);
                }
            }
        }
        finally
        {
            _clipboardGate.Release();
        }
    }

    private void PublishSnapshot(ClipboardSnapshot snapshot)
    {
        uint sequence = snapshot.SequenceNumber;
        if (sequence != 0 && sequence == _lastObservedSequence)
        {
            return;
        }

        if (sequence != 0)
        {
            Volatile.Write(ref _lastObservedSequence, sequence);
        }

        if (snapshot.Payload.IsEmpty)
        {
            return;
        }

        _eventQueue.Enqueue(snapshot);
        StartEventDispatcher();
    }

    private void StartEventDispatcher()
    {
        if (Interlocked.CompareExchange(ref _eventDispatcherActive, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static service => service.DrainEvents(),
            this,
            preferLocal: false);
    }

    private void DrainEvents()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 &&
                   _eventQueue.TryDequeue(out ClipboardSnapshot? snapshot))
            {
                EventHandler<ClipboardChangedEventArgs>? handlers = ClipboardChanged;
                if (handlers is null)
                {
                    continue;
                }

                var args = new ClipboardChangedEventArgs(snapshot);
                foreach (EventHandler<ClipboardChangedEventArgs> handler in
                         handlers.GetInvocationList().Cast<EventHandler<ClipboardChangedEventArgs>>())
                {
                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceError("Clipboard changed handler failed: {0}", exception);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref _eventDispatcherActive, 0);
            if (Volatile.Read(ref _disposed) == 0 && !_eventQueue.IsEmpty)
            {
                StartEventDispatcher();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed class ClipboardWriteBuffer : IDisposable
    {
        internal ClipboardWriteBuffer(uint nativeFormat, GlobalMemory memory)
        {
            NativeFormat = nativeFormat;
            Memory = memory;
        }

        internal uint NativeFormat { get; }

        internal GlobalMemory Memory { get; }

        public void Dispose()
        {
            Memory.Dispose();
        }
    }
}
