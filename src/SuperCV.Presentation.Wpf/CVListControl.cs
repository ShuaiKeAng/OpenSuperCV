using System.Diagnostics;
using System.Windows;
using SuperCV.Application.History;
using SuperCV.Application.Settings;
using SuperCV.Domain.Clipboard;
using SuperCV.Domain.History;

namespace SuperCV;

/// <summary>
/// Presentation adapter for the legacy window code. All history rules and persistence live in
/// <see cref="HistoryService"/>; this type only projects immutable entries into existing CV windows.
/// </summary>
public static class CVListControl
{
    private static readonly Dictionary<Guid, CVdata> ItemsById = new();
    private static readonly Stack<CV> ReusableWindows = new(capacity: 12);
    private static readonly LinkedList<CV> ReusableWindowTransitions = new();
    private static HistoryService? _service;
    private static SettingsService? _settingsService;
    private static WeakReference<SuperCVWindow>? _owner;
    private static bool _initialized;
    private static bool _presentationSuspended;
    private static bool _reusableWindowWarmupRunning;
    private static System.Windows.Threading.DispatcherTimer? _reusableWindowTrimTimer;
    private static HistorySnapshot? _lastSnapshot;

    public static int _MaxWindows;

    public static int StartIndex { get; private set; }

    public static ObservableList<CVdata> ListAll { get; } = new();

    public static ObservableList<CVdata> ListNow { get; } = new();

    internal static bool HasOpenPopup() =>
        ListNow.Any(item => item.HasOpenPopup) ||
        ReusableWindows.Any(window => window.HasOpenPopup) ||
        ReusableWindowTransitions.Any(window => window.HasOpenPopup);

    public static event EventHandler? ListALLChanged;

    public static event EventHandler? ListNowChanged;

    internal static event EventHandler? ScrollStarted;

    internal static event Action<Guid>? PasteRequested;

    internal static CV? RentReusableWindow()
    {
        if (ReusableWindows.Count > 0)
        {
            return ReusableWindows.Pop();
        }

        // An entering item must never reclaim a window that is still leaving. During a
        // single history synchronization the exit is registered before the replacement is
        // created; reclaiming it here cancels the outgoing item's Top/Left/Opacity animation.
        // Let the caller create a temporary window instead. It joins the normal bounded pool
        // only after its own transition has completed.
        return null;
    }

    internal static void RegisterReusableWindowTransition(CV window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ReusableWindowTransitions.Contains(window))
        {
            ReusableWindowTransitions.AddLast(window);
        }
    }

    internal static void UnregisterReusableWindowTransition(CV window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ReusableWindowTransitions.Remove(window);
    }

    internal static void ReturnReusableWindow(CV window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ReusableWindows.Count < ReusableWindowCapacity)
        {
            ReusableWindows.Push(window);
            ScheduleReusableWindowTrim();
            return;
        }

        window.CloseUI(AnimationDirection.None);
    }

    internal static async void BeginReusableWindowWarmup()
    {
        if (!_initialized || _reusableWindowWarmupRunning)
        {
            return;
        }

        _reusableWindowWarmupRunning = true;
        try
        {
            while (_initialized && ReusableWindows.Count < ReusableWindowWarmReserve)
            {
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (!_initialized || _presentationSuspended)
                {
                    break;
                }

                CreateReusableWindow();
            }
        }
        catch (InvalidOperationException) when (!_initialized)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Reusable clipboard window warmup stopped: {0}",
                exception);
        }
        finally
        {
            _reusableWindowWarmupRunning = false;
        }
    }

    private static void WarmReusableWindowsSynchronously()
    {
        if (!_initialized ||
            _presentationSuspended ||
            _reusableWindowWarmupRunning)
        {
            return;
        }

        _reusableWindowWarmupRunning = true;
        try
        {
            while (_initialized &&
                   !_presentationSuspended &&
                   ReusableWindows.Count < ReusableWindowWarmReserve)
            {
                CreateReusableWindow();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Reusable clipboard window restore warmup stopped: {0}",
                exception);
        }
        finally
        {
            _reusableWindowWarmupRunning = false;
        }
    }

    private static void CreateReusableWindow()
    {
        var window = new CV(
            GetOwner(),
            DateTimeOffset.UtcNow,
            string.Empty);
        try
        {
            window.ShowActivated = false;
            window.UpDataIndex(realIndex: 0, selectedIndex: 0, indexUI: 1);
            window.Show();
            if (!window.TryCloseForReuse(AnimationDirection.None))
            {
                window.CloseUI(AnimationDirection.None);
            }
        }
        catch
        {
            window.CloseUI(AnimationDirection.None);
            throw;
        }
    }

    private static int ReusableWindowWarmReserve => Math.Min(4, _MaxWindows);

    private static int ReusableWindowCapacity =>
        Math.Max(ReusableWindowWarmReserve * 2, _MaxWindows + 2);

    public static bool OnTop { get; private set; }

    public static bool OnBottom { get; private set; }

    public static int WheelStill { get; private set; }

    public static bool FilterEnabled { get; private set; }

    public static List<int> SelectedIndexes { get; private set; } = new();

    public static void Init(int maxWindows, SuperCVWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_initialized)
        {
            _owner = new WeakReference<SuperCVWindow>(owner);
            SetPageSize(maxWindows);
            return;
        }

        App app = System.Windows.Application.Current as App
            ?? throw new InvalidOperationException("WPF application runtime is unavailable.");
        _service = app.Runtime.History;
        _settingsService = app.Runtime.Settings;
        _owner = new WeakReference<SuperCVWindow>(owner);
        _service.Changed += OnHistoryChanged;
        _settingsService.Changed += OnSettingsChanged;
        _initialized = true;
        SetPageSize(maxWindows);
        Synchronize(_service.Snapshot);
    }

    public static ClipboardEntry? Add(Dictionary<TextFormat, string> formats)
    {
        return AddAsync(formats).AsTask().GetAwaiter().GetResult();
    }

    public static ValueTask<ClipboardEntry?> AddAsync(
        IReadOnlyDictionary<TextFormat, string> formats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formats);
        var payload = new ClipboardPayload(formats.ToDictionary(
            pair => pair.Key switch
            {
                TextFormat.Text => ClipboardFormat.Text,
                TextFormat.UnicodeText => ClipboardFormat.UnicodeText,
                TextFormat.Html => ClipboardFormat.Html,
                TextFormat.Rtf => ClipboardFormat.Rtf,
                TextFormat.Image => ClipboardFormat.Image,
                _ => throw new ArgumentOutOfRangeException(nameof(formats)),
            },
            pair => pair.Value));

        return payload.IsEmpty
            ? ValueTask.FromResult<ClipboardEntry?>(null)
            : GetService().AddAsync(
                payload,
                allowDuplicate: true,
                cancellationToken: cancellationToken,
                removeOldDuplicateEntries: Setting.RemoveOldDuplicateEntriesOnCopy);
    }

    public static void CreateBlank()
    {
        string text = LocalizationService.Current.T(HistoryService.BlankEntryDefaultText);
        _ = GetService()
            .CreateBlankAsync(text)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public static void RemoveAt(int index)
    {
        if (index < 0 || index >= ListAll.Count)
        {
            return;
        }

        _ = GetService().RemoveAsync(ListAll[index].Id).AsTask().GetAwaiter().GetResult();
    }

    public static void NotifyChanged(CVdata item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = GetService().UpdateAsync(item.ToEntry()).AsTask().GetAwaiter().GetResult();
    }

    public static void Clear()
    {
        GetService().ClearAsync().AsTask().GetAwaiter().GetResult();
    }

    public static void SearchText(string searchString, bool search = false)
    {
        GetService().SetSearch(searchString, search);
    }

    public static void IndexFilter(IEnumerable<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        IReadOnlyList<ClipboardEntry> entries = GetService().Snapshot.Entries;
        Guid[] ids = indexes
            .Where(index => index >= 0 && index < entries.Count)
            .Distinct()
            .Select(index => entries[index].Id)
            .ToArray();
        GetService().SetExternalFilter(ids);
    }

    public static void MoveUp()
    {
        HistorySnapshot snapshot = GetService().Snapshot;
        WheelStill = snapshot.IsAtTop ? 1 : 0;
        if (!snapshot.IsAtTop)
        {
            ScrollStarted?.Invoke(null, EventArgs.Empty);
        }

        GetService().MoveUp();
    }

    public static void MoveDown()
    {
        HistorySnapshot snapshot = GetService().Snapshot;
        WheelStill = snapshot.IsAtBottom ? -1 : 0;
        if (!snapshot.IsAtBottom)
        {
            ScrollStarted?.Invoke(null, EventArgs.Empty);
        }

        GetService().MoveDown();
    }

    public static void MoveTop()
    {
        WheelStill = 0;
        if (GetService().Snapshot.IsAtTop)
        {
            return;
        }

        ScrollStarted?.Invoke(null, EventArgs.Empty);
        GetService().MoveTop();
    }

    public static void SetPageSize(int pageSize)
    {
        _MaxWindows = Math.Clamp(pageSize, 3, 6);
        if (_service is not null)
        {
            _service.SetPageSize(_MaxWindows);
        }

        TrimReusableWindows(ReusableWindowCapacity);
        ScheduleReusableWindowTrim();
    }

    public static void SetMaxItems(int maxItems)
    {
        GetService().SetMaxItems(maxItems);
    }

    internal static void SuspendPresentation()
    {
        if (!_initialized || _presentationSuspended)
        {
            return;
        }

        _presentationSuspended = true;
        WheelStill = 0;
        foreach (CVdata item in ItemsById.Values)
        {
            item.CloseUIImmediately();
        }

        CloseReusableWindows();
    }

    internal static void ResumePresentation()
    {
        if (!_initialized || !_presentationSuspended)
        {
            return;
        }

        _presentationSuspended = false;
        int[] appearanceAnchors = ListNow.Count > 0
            ? [ListAll.IndexOf(ListNow[0])]
            : [];
        foreach (CVdata item in ListNow.ToArray())
        {
            TryUpdateVisibleUI(
                item,
                ResolveVerticalAnimationDirection(
                    ListAll.IndexOf(item),
                    appearanceAnchors));
        }

        // SuspendPresentation closes the spare HWNDs to keep tray memory low. Refill the small
        // transition reserve before input resumes; otherwise the first scroll immediately
        // reclaims its own exiting window and the disappearance animation is never visible.
        WarmReusableWindowsSynchronously();
        ListNow.Changed();
        ListNowChanged?.Invoke(null, EventArgs.Empty);
        WheelStill = 0;
    }

    public static void Shutdown()
    {
        if (_service is not null)
        {
            _service.Changed -= OnHistoryChanged;
        }
        if (_settingsService is not null)
        {
            _settingsService.Changed -= OnSettingsChanged;
        }
        foreach (CVdata item in ListNow.ToArray())
        {
            item.CloseUI(AnimationDirection.None);
        }

        CloseReusableWindows();
        ListNow.ClearOff();
        ListAll.ClearOff();
        ItemsById.Clear();
        _service = null;
        _settingsService = null;
        _owner = null;
        _initialized = false;
        _presentationSuspended = false;
        _reusableWindowWarmupRunning = false;
        _lastSnapshot = null;
        StopReusableWindowTrimTimer();
    }

    private static void CloseReusableWindows()
    {
        foreach (CV window in ReusableWindowTransitions.ToArray())
        {
            window.CloseTransitionImmediately();
        }

        ReusableWindowTransitions.Clear();
        while (ReusableWindows.Count > 0)
        {
            ReusableWindows.Pop().CloseUI(AnimationDirection.None);
        }
    }

    private static void ScheduleReusableWindowTrim()
    {
        if (!_initialized || ReusableWindows.Count <= ReusableWindowWarmReserve)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _reusableWindowTrimTimer ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _reusableWindowTrimTimer.Tick -= ReusableWindowTrimTimer_Tick;
        _reusableWindowTrimTimer.Tick += ReusableWindowTrimTimer_Tick;
        _reusableWindowTrimTimer.Stop();
        _reusableWindowTrimTimer.Start();
    }

    private static void ReusableWindowTrimTimer_Tick(object? sender, EventArgs e)
    {
        _reusableWindowTrimTimer?.Stop();
        if (_initialized && !_presentationSuspended)
        {
            TrimReusableWindows(ReusableWindowWarmReserve);
        }
    }

    private static void TrimReusableWindows(int targetCount)
    {
        while (ReusableWindows.Count > targetCount)
        {
            ReusableWindows.Pop().CloseUI(AnimationDirection.None);
        }
    }

    private static void StopReusableWindowTrimTimer()
    {
        if (_reusableWindowTrimTimer is null)
        {
            return;
        }

        _reusableWindowTrimTimer.Stop();
        _reusableWindowTrimTimer.Tick -= ReusableWindowTrimTimer_Tick;
        _reusableWindowTrimTimer = null;
    }

    internal static SuperCVWindow GetOwner()
    {
        if (_owner is not null && _owner.TryGetTarget(out SuperCVWindow? owner))
        {
            return owner;
        }

        throw new InvalidOperationException("The main window coordinator is unavailable.");
    }

    internal static void NotifyPasteRequested(CVdata item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ListAll.Contains(item))
        {
            PasteRequested?.Invoke(item.Id);
        }
    }

    private static void OnHistoryChanged(object? sender, HistoryChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Synchronize(e.Snapshot));
            return;
        }

        Synchronize(e.Snapshot);
    }

    private static void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(RefreshTextSize);
            return;
        }

        RefreshTextSize();
    }

    internal static void RefreshTextSize()
    {
        ApplyTextSize((double)Setting.TextSize);
    }

    internal static void ApplyTextSize(double textSize)
    {
        if (!_initialized)
        {
            return;
        }

        foreach (CVdata item in ListNow.ToArray())
        {
            item.ApplyTextSize(textSize);
        }

        foreach (CV window in ReusableWindows)
        {
            window.ApplyTextSize(textSize);
        }
    }

    private static void Synchronize(HistorySnapshot snapshot)
    {
        List<CVdata> previousVisible = ListNow.ToList();
        var previousPositions = (_lastSnapshot?.FilteredEntries ?? [])
            .Select((entry, index) => (entry.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        bool viewportOnly = IsViewportOnlyChange(snapshot);
        HashSet<Guid>? retainedIds = null;
        if (!viewportOnly)
        {
            retainedIds = snapshot.Entries.Select(entry => entry.Id).ToHashSet();
            var allItems = new List<CVdata>(snapshot.Entries.Count);
            foreach (ClipboardEntry entry in snapshot.Entries)
            {
                if (!ItemsById.TryGetValue(entry.Id, out CVdata? item))
                {
                    item = CVdata.FromEntry(entry);
                    ItemsById.Add(entry.Id, item);
                }
                else
                {
                    item.ApplyEntry(entry);
                }

                allItems.Add(item);
            }

            ListAll.ClearOff();
            foreach (CVdata item in allItems)
            {
                ListAll.AddOff(item);
            }

            var indexesById = allItems
                .Select((item, index) => (item.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index);
            SelectedIndexes = snapshot.FilteredEntries
                .Select(entry => indexesById[entry.Id])
                .ToList();
        }

        var visibleItems = snapshot.VisibleEntries
            .Select(entry => ItemsById[entry.Id])
            .ToList();
        var currentPositions = snapshot.FilteredEntries
            .Select((entry, index) => (entry.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        HashSet<Guid> previousVisibleIds = previousVisible
            .Select(item => item.Id)
            .ToHashSet();
        HashSet<Guid> visibleIds = visibleItems
            .Select(item => item.Id)
            .ToHashSet();
        Guid[] retainedVisibleIds = previousVisibleIds
            .Intersect(visibleIds)
            .ToArray();
        int[] exitAnchors = GetTransitionAnchors(
            retainedVisibleIds,
            previousPositions,
            visibleItems.Select(item => item.Id),
            previousVisible.Take(1).Select(item => item.Id));
        int[] appearanceAnchors = GetTransitionAnchors(
            retainedVisibleIds,
            currentPositions,
            previousVisible.Select(item => item.Id),
            visibleItems.Take(1).Select(item => item.Id));
        ListNow.ClearOff();
        foreach (CVdata item in visibleItems)
        {
            ListNow.AddOff(item);
        }

        StartIndex = snapshot.StartIndex;
        FilterEnabled = snapshot.IsFiltered;
        OnTop = snapshot.IsAtTop;
        OnBottom = snapshot.IsAtBottom;

        if (_presentationSuspended)
        {
            foreach (CVdata item in previousVisible.Concat(visibleItems).Distinct())
            {
                item.CloseUIImmediately();
            }
        }
        else
        {
            foreach (CVdata previous in previousVisible)
            {
                if (!visibleIds.Contains(previous.Id))
                {
                    AnimationDirection direction = !ListAll.Contains(previous)
                        ? AnimationDirection.Right
                        : previousPositions.TryGetValue(previous.Id, out int abstractIndex)
                            ? ResolveVerticalAnimationDirection(abstractIndex, exitAnchors)
                            : AnimationDirection.None;
                    previous.CloseUI(direction);
                }
            }

            foreach (CVdata item in visibleItems)
            {
                AnimationDirection direction = previousVisibleIds.Contains(item.Id)
                    ? AnimationDirection.None
                    : ResolveVerticalAnimationDirection(
                        currentPositions[item.Id],
                        appearanceAnchors);
                TryUpdateVisibleUI(item, direction);
            }
        }

        if (retainedIds is not null)
        {
            foreach (Guid removedId in ItemsById.Keys
                         .Where(id => !retainedIds.Contains(id))
                         .ToArray())
            {
                ItemsById.Remove(removedId);
            }
        }

        _lastSnapshot = snapshot;
        ListAll.Changed();
        ListALLChanged?.Invoke(null, EventArgs.Empty);
        if (!_presentationSuspended)
        {
            ListNow.Changed();
            ListNowChanged?.Invoke(null, EventArgs.Empty);
        }

        WheelStill = 0;
    }

    private static bool IsViewportOnlyChange(HistorySnapshot snapshot)
    {
        HistorySnapshot? previous = _lastSnapshot;
        return previous is not null &&
               previous.PageSize == snapshot.PageSize &&
               previous.IsFiltered == snapshot.IsFiltered &&
               HaveSameEntryReferences(previous.Entries, snapshot.Entries) &&
               HaveSameEntryReferences(
                   previous.FilteredEntries,
                   snapshot.FilteredEntries);
    }

    private static bool HaveSameEntryReferences(
        IReadOnlyList<ClipboardEntry> left,
        IReadOnlyList<ClipboardEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int[] GetTransitionAnchors(
        IEnumerable<Guid> preferredIds,
        IReadOnlyDictionary<Guid, int> positions,
        IEnumerable<Guid> alternateIds,
        IEnumerable<Guid> fallbackIds)
    {
        int[] anchors = preferredIds
            .Where(positions.ContainsKey)
            .Select(id => positions[id])
            .ToArray();
        if (anchors.Length > 0)
        {
            return anchors;
        }

        anchors = alternateIds
            .Where(positions.ContainsKey)
            .Select(id => positions[id])
            .ToArray();
        return anchors.Length > 0
            ? anchors
            : fallbackIds
                .Where(positions.ContainsKey)
                .Select(id => positions[id])
                .ToArray();
    }

    internal static AnimationDirection ResolveVerticalAnimationDirection(
        int abstractIndex,
        IReadOnlyList<int> anchorIndexes)
    {
        if (abstractIndex < 0 || anchorIndexes.Count == 0)
        {
            return AnimationDirection.None;
        }

        int nearestAnchor = anchorIndexes
            .OrderBy(index => Math.Abs((long)index - abstractIndex))
            .ThenByDescending(index => index)
            .First();
        return abstractIndex <= nearestAnchor
            ? AnimationDirection.UP
            : AnimationDirection.Down;
    }

    private static void TryUpdateVisibleUI(
        CVdata item,
        AnimationDirection appearanceDirection)
    {
        try
        {
            item.UpdateVisibleUI(appearanceDirection);
        }
        catch (Exception exception)
        {
            item.CloseUIImmediately();
            Trace.TraceError(
                "Failed to create clipboard item window {0}: {1}",
                item.Id,
                exception);
        }
    }

    private static HistoryService GetService() =>
        _service ?? throw new InvalidOperationException("History adapter is not initialized.");
}
