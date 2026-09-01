using SuperCV.Application.Bookmarks;
using SuperCV.Domain.Bookmarks;
using SuperCV.Domain.Clipboard;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SuperCV;

/// <summary>
/// Preserves the legacy presentation API while delegating bookmark state and persistence to
/// <see cref="BookmarksService"/>. The UI is held weakly so the application service cannot keep
/// the main window alive after it has been unloaded.
/// </summary>
public static class BookmarksControl
{
    public static List<BookmarksData> bookmarksDatas = new();

    private static WeakReference<ItemsControl>? _bookmarkListReference;
    private static BookmarksService? _service;
    private static BookmarkDragReorderController? _dragController;
    private static int _compositionGeneration;

    public static void Init(ItemsControl bookmarkList)
    {
        ArgumentNullException.ThrowIfNull(bookmarkList);
        if (System.Windows.Application.Current is not App app)
        {
            throw new InvalidOperationException("The bookmark adapter requires the application runtime.");
        }

        Detach();

        _service = app.Runtime.Bookmarks;
        _bookmarkListReference = new WeakReference<ItemsControl>(bookmarkList);
        _dragController = new BookmarkDragReorderController(
            bookmarkList,
            (id, targetIndex) => GetService().MoveAsync(id, targetIndex),
            exception => ShowFailure("调整书签顺序失败", exception));
        bookmarkList.Unloaded += OnBookmarkListUnloaded;
        _service.Changed += OnBookmarksChanged;

        Synchronize(_service.Snapshot);
    }

    public static void AddBookmark(
        string title,
        Dictionary<TextFormat, string> text,
        string launchTime)
    {
        _ = launchTime; // Retained for the existing call contract; creation time is owned by the domain.

        try
        {
            ClipboardPayload payload = ToPayload(text);
            if (payload.IsEmpty)
            {
                return;
            }

            _ = AddBookmarkCoreAsync(title, payload);
        }
        catch (Exception exception)
        {
            ShowFailure("添加书签失败", exception);
        }
    }

    /// <summary>
    /// Explicitly releases the adapter subscription. The weak UI reference already prevents a
    /// window leak, while this method also makes repeated composition and tests deterministic.
    /// </summary>
    public static void Shutdown() => Detach();

    private static CapsuleButton CreateBookmarkButton(Bookmark bookmark)
    {
        var capsuleButton = new CapsuleButton
        {
            MaxWidth = 150,
            Text = bookmark.Title,
            CornerRadius = new CornerRadius(11),
            // CapsuleButton reserves its own 4 px shadow gutter. Keeping the item margin at zero
            // preserves the original 8 px visual gap and 30 px row height without clipping.
            Margin = new Thickness(0),
            Tag = bookmark.Id,
            Cursor = Cursors.Hand,
        };

        capsuleButton.SetResourceReference(CapsuleButton.CapsuleBackgroundProperty, "Brush.Surface.Subtle");
        capsuleButton.EditClicked += OnEditClicked;
        capsuleButton.TextClicked += OnTextClicked;
        capsuleButton.CloseClicked += OnCloseClicked;
        return capsuleButton;
    }

    private static async void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetBookmark(sender, out Bookmark? bookmark))
        {
            return;
        }

        var dialog = new TextBox2Dialog(
            title: "设置书签",
            tip1: "请输入标签名",
            tip2: "请输入内容",
            label: bookmark.Title,
            prompt: bookmark.Payload.PrimaryText);

        if (!dialog.ShowDialog())
        {
            return;
        }

        try
        {
            var payload = new ClipboardPayload(
                new Dictionary<ClipboardFormat, string>
                {
                    [ClipboardFormat.UnicodeText] = dialog.Prompt,
                });
            var updated = new Bookmark(
                bookmark.Id,
                dialog.LabelName,
                payload,
                bookmark.CreatedAtUtc);

            await GetService().UpdateAsync(updated);
        }
        catch (Exception exception)
        {
            ShowFailure("修改书签失败", exception);
        }
    }

    private static async void OnTextClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetBookmark(sender, out Bookmark? bookmark))
            {
                return;
            }

            GlobalFocusManager.RestorePreviousFocus();
            BookmarksData data = ToPresentationData(bookmark);
            await Task.Delay(100);
            data.OnPaste(sender, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ShowFailure("粘贴书签失败", exception);
        }
    }

    private static async void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        if (!TryGetBookmark(sender, out Bookmark? bookmark))
        {
            return;
        }

        var alertDialog = new AlertDialog($"确定删除\"{bookmark.Title}\"？");
        if (!alertDialog.ShowDialog())
        {
            return;
        }

        try
        {
            await GetService().RemoveAsync(bookmark.Id);
        }
        catch (Exception exception)
        {
            ShowFailure("删除书签失败", exception);
        }
    }

    private static void OnBookmarksChanged(object? sender, BookmarksChangedEventArgs e)
    {
        Bookmark[] snapshot = e.Bookmarks.ToArray();
        int generation = Volatile.Read(ref _compositionGeneration);
        if (!TryGetBookmarkList(out ItemsControl? bookmarkList))
        {
            return;
        }

        if (bookmarkList.Dispatcher.HasShutdownStarted ||
            bookmarkList.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (bookmarkList.Dispatcher.CheckAccess())
        {
            Synchronize(snapshot);
            return;
        }

        _ = bookmarkList.Dispatcher.BeginInvoke(() =>
        {
            if (generation == Volatile.Read(ref _compositionGeneration))
            {
                Synchronize(snapshot);
            }
        });
    }

    private static void Synchronize(IReadOnlyList<Bookmark> snapshot)
    {
        if (!TryGetBookmarkList(out ItemsControl? bookmarkList))
        {
            return;
        }

        _dragController?.HandleCollectionChanging();
        var desiredIds = snapshot.Select(bookmark => bookmark.Id).ToHashSet();
        CapsuleButton[] staleButtons = bookmarkList.Items
            .OfType<CapsuleButton>()
            .Where(button => button.Tag is not Guid id || !desiredIds.Contains(id))
            .ToArray();
        foreach (CapsuleButton staleButton in staleButtons)
        {
            bookmarkList.Items.Remove(staleButton);
        }

        var buttonsById = new Dictionary<Guid, CapsuleButton>();
        foreach (CapsuleButton button in bookmarkList.Items.OfType<CapsuleButton>().ToArray())
        {
            if (button.Tag is Guid id && !buttonsById.TryAdd(id, button))
            {
                bookmarkList.Items.Remove(button);
            }
        }

        for (int index = 0; index < snapshot.Count; index++)
        {
            Bookmark bookmark = snapshot[index];
            if (!buttonsById.TryGetValue(bookmark.Id, out CapsuleButton? button))
            {
                button = CreateBookmarkButton(bookmark);
                bookmarkList.Items.Insert(Math.Min(index, bookmarkList.Items.Count), button);
            }
            else
            {
                button.Text = bookmark.Title;
                int currentIndex = bookmarkList.Items.IndexOf(button);
                if (currentIndex != index)
                {
                    bookmarkList.Items.RemoveAt(currentIndex);
                    bookmarkList.Items.Insert(Math.Min(index, bookmarkList.Items.Count), button);
                }
            }
        }

        bookmarksDatas.Clear();
        foreach (Bookmark bookmark in snapshot)
        {
            bookmarksDatas.Add(ToPresentationData(bookmark));
        }
    }

    private static bool TryGetBookmark(
        object? sender,
        [NotNullWhen(true)] out Bookmark? bookmark)
    {
        bookmark = null;
        if (sender is not FrameworkElement { Tag: Guid id })
        {
            return false;
        }

        try
        {
            bookmark = GetService().Snapshot.FirstOrDefault(item => item.Id == id);
            return bookmark is not null;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static BookmarksData ToPresentationData(Bookmark bookmark) =>
        new(
            ToLegacyFormats(bookmark.Payload),
            bookmark.CreatedAtUtc.ToLocalTime().ToString(),
            bookmark.Title);

    private static ClipboardPayload ToPayload(
        IReadOnlyDictionary<TextFormat, string>? formats)
    {
        if (formats is null || formats.Count == 0)
        {
            return ClipboardPayload.Empty;
        }

        return new ClipboardPayload(
            formats.ToDictionary(
                pair => ToDomainFormat(pair.Key),
                pair => pair.Value));
    }

    private static Dictionary<TextFormat, string> ToLegacyFormats(ClipboardPayload payload) =>
        payload.Formats.ToDictionary(
            pair => pair.Key switch
            {
                ClipboardFormat.Text => TextFormat.Text,
                ClipboardFormat.UnicodeText => TextFormat.UnicodeText,
                ClipboardFormat.Html => TextFormat.Html,
                ClipboardFormat.Rtf => TextFormat.Rtf,
                ClipboardFormat.Image => TextFormat.Image,
                _ => throw new ArgumentOutOfRangeException(nameof(payload)),
            },
            pair => pair.Value);

    private static ClipboardFormat ToDomainFormat(TextFormat format) => format switch
    {
        TextFormat.Text => ClipboardFormat.Text,
        TextFormat.UnicodeText => ClipboardFormat.UnicodeText,
            TextFormat.Html => ClipboardFormat.Html,
            TextFormat.Rtf => ClipboardFormat.Rtf,
            TextFormat.Image => ClipboardFormat.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static BookmarksService GetService() =>
        _service ?? throw new InvalidOperationException("The bookmark adapter has not been initialized.");

    private static async Task AddBookmarkCoreAsync(string title, ClipboardPayload payload)
    {
        try
        {
            await GetService().AddAsync(title, payload);
        }
        catch (Exception exception)
        {
            ShowFailure("添加书签失败", exception);
        }
    }

    private static bool TryGetBookmarkList(
        [NotNullWhen(true)] out ItemsControl? bookmarkList)
    {
        bookmarkList = null;
        return _bookmarkListReference?.TryGetTarget(out bookmarkList) == true;
    }

    private static void OnBookmarkListUnloaded(object sender, RoutedEventArgs e)
    {
        if (TryGetBookmarkList(out ItemsControl? bookmarkList) &&
            ReferenceEquals(sender, bookmarkList))
        {
            Detach();
        }
    }

    private static void Detach()
    {
        Interlocked.Increment(ref _compositionGeneration);
        if (_service is not null)
        {
            _service.Changed -= OnBookmarksChanged;
            _service = null;
        }

        _dragController?.Dispose();
        _dragController = null;
        if (TryGetBookmarkList(out ItemsControl? bookmarkList))
        {
            bookmarkList.Unloaded -= OnBookmarkListUnloaded;
        }

        _bookmarkListReference = null;
        bookmarksDatas.Clear();
    }

    private static void ShowFailure(string operation, Exception exception)
    {
        string message = exception.GetBaseException().Message;
        new AlertDialog($"{operation}：{message}").ShowDialog();
    }
}
