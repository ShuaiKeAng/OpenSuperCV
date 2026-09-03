using SuperCV.Application.History;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SuperCV;

public sealed class PersistentHistoryItemViewModel : INotifyPropertyChanged
{
    private const int MaximumPreviewLength = 512;
    private long _displayId;

    internal PersistentHistoryItemViewModel(PersistentHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        StorageKey = item.StorageKey;
        _displayId = item.DisplayId;
        StorageText = item.Text;
        IsImage = PersistentHistoryCodec.TryDecodeImage(item.Text, out string imageLink);
        Text = IsImage ? imageLink : item.Text;
        CapturedAtUtc = item.CapturedAtUtc;
        DisplayText = CreateSingleLinePreview(Text);
        CreatedAtDisplay = item.CapturedAtUtc
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    internal long StorageKey { get; }

    internal DateTimeOffset CapturedAtUtc { get; }

    public long DisplayId
    {
        get => _displayId;
        internal set
        {
            if (_displayId == value)
            {
                return;
            }

            _displayId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayId)));
        }
    }

    internal string StorageText { get; }

    public bool IsImage { get; }

    public string Text { get; }

    public string DisplayText { get; }

    public string CreatedAtDisplay { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string CreateSingleLinePreview(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start == text.Length)
        {
            return string.Empty;
        }

        int available = Math.Min(MaximumPreviewLength, text.Length - start);
        var preview = new StringBuilder(available + 1);
        for (int index = start; index < start + available; index++)
        {
            char character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                preview.Append(' ');
            }
            else if (character == '\n')
            {
                preview.Append(' ');
            }
            else
            {
                preview.Append(character);
            }
        }

        if (start + available < text.Length)
        {
            preview.Append('…');
        }

        return preview.ToString();
    }
}

public sealed class PersistentHistoryViewModel : INotifyPropertyChanged, IDisposable
{
    internal const int PageSize = 100;

    private readonly PersistentHistoryService _service;
    private readonly HistoryService? _synchronizedHistory;
    private CancellationTokenSource? _loadCancellation;
    private PersistentHistoryCursor? _cursor;
    private Guid _workspaceId;
    private string _searchText = string.Empty;
    private string _appliedSearchText = string.Empty;
    private DateTime? _startDate;
    private DateTime? _endDate;
    private PersistentHistoryDateRange? _appliedDateRange;
    private string _statusText = "打开历史记录后按需加载";
    private bool _isLoading;
    private bool _initialized;
    private bool _disposed;
    private int _totalCount;

    internal PersistentHistoryViewModel(
        PersistentHistoryService service,
        Guid workspaceId)
        : this(service, synchronizedHistory: null, workspaceId)
    {
    }

    internal PersistentHistoryViewModel(
        PersistentHistoryService service,
        HistoryService? synchronizedHistory,
        Guid workspaceId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _synchronizedHistory = synchronizedHistory;
        ValidateWorkspaceId(workspaceId);
        _workspaceId = workspaceId;
        LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
    }

    public ObservableCollection<PersistentHistoryItemViewModel> Entries { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = normalized;
            OnPropertyChanged();
        }
    }

    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            DateTime? normalized = value?.Date;
            if (_startDate == normalized)
            {
                return;
            }

            _startDate = normalized;
            if (_endDate is { } endDate && normalized is { } startDate && endDate < startDate)
            {
                _endDate = startDate;
                OnPropertyChanged(nameof(EndDate));
            }

            OnPropertyChanged();
        }
    }

    public DateTime? EndDate
    {
        get => _endDate;
        set
        {
            DateTime? normalized = value?.Date;
            if (_endDate == normalized)
            {
                return;
            }

            _endDate = normalized;
            if (_startDate is { } startDate && normalized is { } endDate && endDate < startDate)
            {
                _startDate = endDate;
                OnPropertyChanged(nameof(StartDate));
            }

            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _cursor is not null
            ? LocalizationService.Current.IsEnglish
                ? $"Loaded {Entries.Count:N0} / {_totalCount:N0}; scroll down to load more"
                : $"已加载 {Entries.Count:N0} / {_totalCount:N0}，向下滚动继续加载"
            : LocalizationService.Current.T(_statusText);
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    public bool HasMore => _cursor is not null;

    public bool IsEmpty => _initialized && !IsLoading && Entries.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task EnsureLoadedAsync() =>
        _initialized ? Task.CompletedTask : ReloadAsync();

    internal async Task ChangeWorkspaceAsync(Guid workspaceId)
    {
        ValidateWorkspaceId(workspaceId);
        if (_workspaceId == workspaceId && _initialized)
        {
            return;
        }

        SelectWorkspace(workspaceId);
        await ReloadAsync();
    }

    internal void SelectWorkspace(Guid workspaceId)
    {
        ValidateWorkspaceId(workspaceId);
        if (_workspaceId == workspaceId)
        {
            return;
        }

        CancelCurrentLoad();
        _workspaceId = workspaceId;
        _initialized = false;
        _cursor = null;
        _totalCount = 0;
        IsLoading = false;
        Entries.Clear();
        NotifyCollectionStateChanged();
    }

    internal Task SearchAsync() => ReloadAsync();

    internal void Invalidate()
    {
        ThrowIfDisposed();
        CancelCurrentLoad();
        _initialized = false;
        _cursor = null;
        _totalCount = 0;
        IsLoading = false;
        Entries.Clear();
        NotifyCollectionStateChanged();
    }

    internal async Task ReloadAsync()
    {
        ThrowIfDisposed();
        CancellationTokenSource requestCancellation = BeginLoad();
        _initialized = true;
        _appliedSearchText = SearchText.Trim();
        _appliedDateRange = CreateDateRange(StartDate, EndDate);
        _cursor = null;
        Entries.Clear();
        _totalCount = 0;
        NotifyCollectionStateChanged();

        try
        {
            PersistentHistoryPage page = await _service.QueryAsync(
                _workspaceId,
                _appliedSearchText,
                PageSize,
                cursor: null,
                dateRange: _appliedDateRange,
                cancellationToken: requestCancellation.Token);
            if (!ReferenceEquals(_loadCancellation, requestCancellation))
            {
                return;
            }

            ApplyFirstPage(page);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteLoad(requestCancellation);
        }
    }

    internal async Task LoadMoreAsync()
    {
        ThrowIfDisposed();
        if (IsLoading || _cursor is null)
        {
            return;
        }

        PersistentHistoryCursor cursor = _cursor;
        CancellationTokenSource requestCancellation = BeginLoad();
        try
        {
            PersistentHistoryPage page = await _service.QueryAsync(
                _workspaceId,
                _appliedSearchText,
                PageSize,
                cursor,
                _appliedDateRange,
                requestCancellation.Token);
            if (!ReferenceEquals(_loadCancellation, requestCancellation))
            {
                return;
            }

            MergePageNewestFirst(page.Items);

            _cursor = page.NextCursor;
            _totalCount = page.TotalCount;
            NotifyCollectionStateChanged();
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteLoad(requestCancellation);
        }
    }

    internal async Task DeleteAsync(PersistentHistoryItemViewModel item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        CancelCurrentLoad();

        bool removed;
        if (_synchronizedHistory is null)
        {
            removed = await _service.DeleteAsync(_workspaceId, item.StorageKey);
        }
        else
        {
            removed = await _synchronizedHistory.RemovePersistentEntryAsync(
                _workspaceId,
                item.StorageKey,
                item.CapturedAtUtc,
                item.StorageText);
        }

        if (removed && Entries.Remove(item))
        {
            _totalCount = Math.Max(0, _totalCount - 1);
            RenumberEntries();
            if (_cursor is not null)
            {
                _cursor = _cursor with
                {
                    NextDisplayId = Math.Max(1, _cursor.NextDisplayId - 1),
                    TotalCount = _totalCount,
                };
            }

            NotifyCollectionStateChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _loadCancellation,
            null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private CancellationTokenSource BeginLoad()
    {
        CancelCurrentLoad();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        IsLoading = true;
        UpdateStatus();
        return cancellation;
    }

    private void CompleteLoad(CancellationTokenSource requestCancellation)
    {
        if (ReferenceEquals(_loadCancellation, requestCancellation))
        {
            _loadCancellation = null;
            IsLoading = false;
            NotifyCollectionStateChanged();
        }

        requestCancellation.Dispose();
    }

    private void CancelCurrentLoad()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _loadCancellation,
            null);
        previous?.Cancel();
    }

    private void ApplyFirstPage(PersistentHistoryPage page)
    {
        MergePageNewestFirst(page.Items);

        _cursor = page.NextCursor;
        _totalCount = page.TotalCount;
        NotifyCollectionStateChanged();
    }

    private void MergePageNewestFirst(IReadOnlyList<PersistentHistoryItem> items)
    {
        foreach (PersistentHistoryItem item in items)
        {
            var viewModel = new PersistentHistoryItemViewModel(item);
            int low = 0;
            int high = Entries.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                PersistentHistoryItemViewModel existing = Entries[middle];
                bool belongsBeforeExisting =
                    viewModel.CapturedAtUtc > existing.CapturedAtUtc ||
                    (viewModel.CapturedAtUtc == existing.CapturedAtUtc &&
                     viewModel.StorageKey > existing.StorageKey);
                if (belongsBeforeExisting)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            Entries.Insert(low, viewModel);
        }
    }

    private void RenumberEntries()
    {
        for (int index = 0; index < Entries.Count; index++)
        {
            Entries[index].DisplayId = index + 1;
        }
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (IsLoading && Entries.Count == 0)
        {
            StatusText = "正在加载…";
            return;
        }

        if (_initialized && Entries.Count == 0)
        {
            StatusText = _appliedSearchText.Length == 0 && _appliedDateRange is null
                ? "当前工作区暂无持久历史"
                : "未找到匹配条目";
            return;
        }

        if (_cursor is not null)
        {
            StatusText = $"已加载 {Entries.Count:N0} / {_totalCount:N0}，向下滚动继续加载";
            return;
        }

        StatusText = _initialized
            ? $"共 {_totalCount:N0} 条"
            : "打开历史记录后按需加载";
    }

    private static PersistentHistoryDateRange? CreateDateRange(
        DateTime? startDate,
        DateTime? endDate)
    {
        if (startDate is null && endDate is null)
        {
            return null;
        }

        return new PersistentHistoryDateRange(
            startDate is { } start ? ConvertLocalDateToUtc(start) : null,
            endDate is { } end ? ConvertLocalDateToUtc(end.AddDays(1)) : null);
    }

    private static DateTimeOffset ConvertLocalDateToUtc(DateTime date)
    {
        DateTime localDate = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate))
            .ToUniversalTime();
    }

    private static void ValidateWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
