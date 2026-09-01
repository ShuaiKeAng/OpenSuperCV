using System.IO;
using System.Windows.Media.Imaging;

namespace SuperCV;

internal sealed record CachedClipboardImage(
    BitmapSource Source,
    int PixelWidth,
    int PixelHeight);

internal static class ClipboardImageVisualCache
{
    private const int DecodePixelWidth = 320;
    private const int MaximumCachedImages = 8;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static readonly Dictionary<string, CacheEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recency = new();

    internal static bool TryGet(string imageLink, out CachedClipboardImage? image)
    {
        image = null;
        string? path = NormalizePath(imageLink);
        if (path is null)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out CacheEntry? entry) ||
                !entry.TryGetImage(out CachedClipboardImage? cachedImage))
            {
                return false;
            }

            Touch(entry);
            image = cachedImage;
            return true;
        }
    }

    internal static Task<CachedClipboardImage?> GetAsync(string imageLink)
    {
        string? path = NormalizePath(imageLink);
        if (path is null)
        {
            return Task.FromResult<CachedClipboardImage?>(null);
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(path, out CacheEntry? existing))
            {
                Touch(existing);
                if (existing.TryGetImage(out CachedClipboardImage? cachedImage))
                {
                    return Task.FromResult(cachedImage);
                }

                if (existing.LoadTask is Task<CachedClipboardImage?> activeLoad)
                {
                    return activeLoad;
                }

                Recency.Remove(existing.Node);
                Entries.Remove(path);
            }

            var cancellation = new CancellationTokenSource();
            var node = Recency.AddFirst(path);
            var entry = new CacheEntry(cancellation, node);
            Entries.Add(path, entry);
            Task<CachedClipboardImage?> loadTask = LoadAndCacheAsync(
                path,
                entry,
                cancellation.Token);
            entry.LoadTask = loadTask;
            Trim();
            return loadTask;
        }
    }

    internal static CachedClipboardImage? Decode(string? imageLink)
    {
        string? path = NormalizePath(imageLink);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            int sourcePixelWidth;
            int sourcePixelHeight;
            using (var metadataStream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                BitmapDecoder metadataDecoder = BitmapDecoder.Create(
                    metadataStream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.None);
                if (metadataDecoder.Frames.Count == 0)
                {
                    return null;
                }

                sourcePixelWidth = metadataDecoder.Frames[0].PixelWidth;
                sourcePixelHeight = metadataDecoder.Frames[0].PixelHeight;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = DecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new CachedClipboardImage(
                image,
                sourcePixelWidth,
                sourcePixelHeight);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                FormatException or
                InvalidOperationException)
        {
            return null;
        }
    }

    internal static Task<BitmapSource?> LoadFullResolutionAsync(
        string imageLink,
        CancellationToken cancellationToken = default)
    {
        string? path = NormalizePath(imageLink);
        if (path is null)
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        return Task.Run(
            () => DecodeFullResolutionPath(path),
            cancellationToken);
    }

    internal static BitmapSource? DecodeFullResolution(string? imageLink)
    {
        string? path = NormalizePath(imageLink);
        return path is null ? null : DecodeFullResolutionPath(path);
    }

    private static async Task<CachedClipboardImage?> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task
                    .Run(() => Decode(path), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                DecodeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<CachedClipboardImage?> LoadAndCacheAsync(
        string path,
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        CachedClipboardImage? image = await LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out CacheEntry? current) ||
                !ReferenceEquals(current, entry))
            {
                return image;
            }

            entry.LoadTask = null;
            entry.DisposeCancellation();
            if (image is null)
            {
                Entries.Remove(path);
                Recency.Remove(entry.Node);
            }
            else
            {
                entry.SetImage(image);
            }
        }

        return image;
    }

    private static BitmapSource? DecodeFullResolutionPath(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                FormatException or
                InvalidOperationException)
        {
            return null;
        }
    }

    private static void Touch(CacheEntry entry)
    {
        Recency.Remove(entry.Node);
        Recency.AddFirst(entry.Node);
    }

    private static void Trim()
    {
        while (Entries.Count > MaximumCachedImages &&
               Recency.Last is LinkedListNode<string> oldest)
        {
            Recency.RemoveLast();
            if (Entries.Remove(oldest.Value, out CacheEntry? removed))
            {
                removed.CancelLoad();
            }
        }
    }

    private static string? NormalizePath(string? imageLink)
    {
        if (string.IsNullOrWhiteSpace(imageLink))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(imageLink);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return null;
        }
    }

    private sealed class CacheEntry
    {
        private WeakReference<CachedClipboardImage>? _image;
        private CancellationTokenSource? _cancellation;

        internal CacheEntry(
            CancellationTokenSource cancellation,
            LinkedListNode<string> node)
        {
            _cancellation = cancellation;
            Node = node;
        }

        internal Task<CachedClipboardImage?>? LoadTask { get; set; }

        internal LinkedListNode<string> Node { get; }

        internal bool TryGetImage(out CachedClipboardImage? image)
        {
            image = null;
            return _image is not null && _image.TryGetTarget(out image);
        }

        internal void SetImage(CachedClipboardImage image)
        {
            _image = new WeakReference<CachedClipboardImage>(image);
        }

        internal void CancelLoad()
        {
            CancellationTokenSource? cancellation = _cancellation;
            _cancellation = null;
            if (cancellation is null)
            {
                return;
            }

            cancellation.Cancel();
            cancellation.Dispose();
        }

        internal void DisposeCancellation()
        {
            CancellationTokenSource? cancellation = _cancellation;
            _cancellation = null;
            cancellation?.Dispose();
        }
    }
}
