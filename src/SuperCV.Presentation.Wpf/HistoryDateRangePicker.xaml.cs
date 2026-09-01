using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SuperCV;

public partial class HistoryDateRangePicker : UserControl, INotifyPropertyChanged
{
    private enum DateEndpoint
    {
        Start,
        End,
    }

    public static readonly DependencyProperty StartDateProperty =
        DependencyProperty.Register(
            nameof(StartDate),
            typeof(DateTime?),
            typeof(HistoryDateRangePicker),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnDatePropertyChanged));

    public static readonly DependencyProperty EndDateProperty =
        DependencyProperty.Register(
            nameof(EndDate),
            typeof(DateTime?),
            typeof(HistoryDateRangePicker),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnDatePropertyChanged));

    public static readonly RoutedEvent DateRangeChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(DateRangeChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(HistoryDateRangePicker));

    private DateEndpoint _activeEndpoint;
    private DateTime _displayMonth = FirstDayOfMonth(DateTime.Today);
    private bool _isLocalizationSubscribed;

    public HistoryDateRangePicker()
    {
        InitializeComponent();
        CalendarPopup.PlacementTarget = PickerSurface;
        CalendarPopup.CustomPopupPlacementCallback = PlaceCalendarPopup;
        Loaded += HistoryDateRangePicker_Loaded;
        Unloaded += HistoryDateRangePicker_Unloaded;
        BuildCalendarDays();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event RoutedEventHandler DateRangeChanged
    {
        add => AddHandler(DateRangeChangedEvent, value);
        remove => RemoveHandler(DateRangeChangedEvent, value);
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StartDateText));
        OnPropertyChanged(nameof(EndDateText));
        OnPropertyChanged(nameof(ActiveTitle));
        OnPropertyChanged(nameof(DisplayMonthText));
        BuildCalendarDays();
    }

    private void HistoryDateRangePicker_Unloaded(object sender, RoutedEventArgs e)
    {
        CalendarPopup.IsOpen = false;
        if (_isLocalizationSubscribed)
        {
            LocalizationService.Current.LanguageChanged -= Localization_LanguageChanged;
            _isLocalizationSubscribed = false;
        }
    }

    private void HistoryDateRangePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isLocalizationSubscribed)
        {
            LocalizationService.Current.LanguageChanged += Localization_LanguageChanged;
            _isLocalizationSubscribed = true;
        }

        Localization_LanguageChanged(this, EventArgs.Empty);
    }

    public DateTime? StartDate
    {
        get => (DateTime?)GetValue(StartDateProperty);
        set => SetValue(StartDateProperty, value);
    }

    public DateTime? EndDate
    {
        get => (DateTime?)GetValue(EndDateProperty);
        set => SetValue(EndDateProperty, value);
    }

    public ObservableCollection<HistoryCalendarDay> CalendarDays { get; } = [];

    public string StartDateText => FormatDate(StartDate, LocalizationService.Current.T("开始日期"));

    public string EndDateText => FormatDate(EndDate, LocalizationService.Current.T("截止日期"));

    public string ActiveTitle =>
        _activeEndpoint == DateEndpoint.Start
            ? LocalizationService.Current.T("选择起始日期")
            : LocalizationService.Current.T("选择截止日期");

    public string DisplayMonthText => LocalizationService.Current.IsEnglish
        ? _displayMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
        : _displayMonth.ToString("yyyy年M月", CultureInfo.CurrentCulture);

    // The field buttons are created before CalendarPopup while InitializeComponent
    // parses the XAML, so their bindings can evaluate these properties first.
    public bool IsStartPickerOpen =>
        CalendarPopup?.IsOpen == true && _activeEndpoint == DateEndpoint.Start;

    public bool IsEndPickerOpen =>
        CalendarPopup?.IsOpen == true && _activeEndpoint == DateEndpoint.End;

    public bool CanClearActiveDate => GetActiveDate() is not null;

    private static void OnDatePropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (HistoryDateRangePicker)dependencyObject;
        picker.OnPropertyChanged(
            eventArgs.Property == StartDateProperty ? nameof(StartDateText) : nameof(EndDateText));
        picker.OnPropertyChanged(nameof(CanClearActiveDate));
        picker.BuildCalendarDays();
    }

    private void StartDateButton_Click(object sender, RoutedEventArgs e) =>
        ToggleCalendar(DateEndpoint.Start);

    private void EndDateButton_Click(object sender, RoutedEventArgs e) =>
        ToggleCalendar(DateEndpoint.End);

    private void ToggleCalendar(DateEndpoint endpoint)
    {
        if (CalendarPopup.IsOpen && _activeEndpoint == endpoint)
        {
            CalendarPopup.IsOpen = false;
            return;
        }

        _activeEndpoint = endpoint;
        DateTime displayDate = GetActiveDate()
            ?? (endpoint == DateEndpoint.Start ? EndDate : StartDate)
            ?? DateTime.Today;
        SetDisplayMonth(displayDate);
        NotifyPopupStateChanged();
        CalendarPopup.IsOpen = true;
    }

    private void PreviousMonthButton_Click(object sender, RoutedEventArgs e) =>
        SetDisplayMonth(_displayMonth.AddMonths(-1));

    private void NextMonthButton_Click(object sender, RoutedEventArgs e) =>
        SetDisplayMonth(_displayMonth.AddMonths(1));

    private void CalendarDayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryCalendarDay day })
        {
            ApplyDate(day.Date);
        }
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e) => ApplyDate(DateTime.Today);

    private void ClearDateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEndpoint == DateEndpoint.Start)
        {
            SetCurrentValue(StartDateProperty, null);
        }
        else
        {
            SetCurrentValue(EndDateProperty, null);
        }

        CompleteUserChange();
    }

    private void ApplyDate(DateTime date)
    {
        DateTime selectedDate = date.Date;
        if (_activeEndpoint == DateEndpoint.Start)
        {
            SetCurrentValue(StartDateProperty, (DateTime?)selectedDate);
            if (EndDate is { } endDate && endDate.Date < selectedDate)
            {
                SetCurrentValue(EndDateProperty, (DateTime?)selectedDate);
            }
        }
        else
        {
            SetCurrentValue(EndDateProperty, (DateTime?)selectedDate);
            if (StartDate is { } startDate && startDate.Date > selectedDate)
            {
                SetCurrentValue(StartDateProperty, (DateTime?)selectedDate);
            }
        }

        CompleteUserChange();
    }

    private void CompleteUserChange()
    {
        CalendarPopup.IsOpen = false;
        RaiseEvent(new RoutedEventArgs(DateRangeChangedEvent, this));
    }

    private void CalendarPopup_Opened(object sender, EventArgs e)
    {
        NotifyPopupStateChanged();
        BuildCalendarDays();
    }

    private void CalendarPopup_Closed(object sender, EventArgs e) => NotifyPopupStateChanged();

    private void CalendarPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CalendarPopup.IsOpen = false;
    }

    private void NotifyPopupStateChanged()
    {
        OnPropertyChanged(nameof(ActiveTitle));
        OnPropertyChanged(nameof(IsStartPickerOpen));
        OnPropertyChanged(nameof(IsEndPickerOpen));
        OnPropertyChanged(nameof(CanClearActiveDate));
    }

    private void SetDisplayMonth(DateTime date)
    {
        _displayMonth = FirstDayOfMonth(date);
        OnPropertyChanged(nameof(DisplayMonthText));
        BuildCalendarDays();
    }

    private void BuildCalendarDays()
    {
        DateTime firstDay = FirstDayOfMonth(_displayMonth);
        int offsetFromMonday = ((int)firstDay.DayOfWeek + 6) % 7;
        DateTime gridStart = firstDay.AddDays(-offsetFromMonday);
        DateTime? selectedDate = GetActiveDate()?.Date;
        DateTime? startDate = StartDate?.Date;
        DateTime? endDate = EndDate?.Date;

        CalendarDays.Clear();
        for (int index = 0; index < 42; index++)
        {
            DateTime date = gridStart.AddDays(index);
            bool isInRange =
                startDate is { } start &&
                endDate is { } end &&
                date >= start &&
                date <= end;
            CalendarDays.Add(new HistoryCalendarDay(
                date,
                date.Month == firstDay.Month,
                date == DateTime.Today,
                selectedDate == date,
                isInRange));
        }
    }

    private DateTime? GetActiveDate() =>
        _activeEndpoint == DateEndpoint.Start ? StartDate : EndDate;

    private CustomPopupPlacement[] PlaceCalendarPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        double centeredX = (targetSize.Width - popupSize.Width) / 2;
        return
        [
            new CustomPopupPlacement(
                new Point(centeredX, targetSize.Height + 4),
                PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(
                new Point(centeredX, -popupSize.Height - 4),
                PopupPrimaryAxis.Vertical),
        ];
    }

    private static string FormatDate(DateTime? date, string placeholder) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? placeholder;

    private static DateTime FirstDayOfMonth(DateTime date) =>
        new(date.Year, date.Month, 1);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class HistoryCalendarDay
{
    internal HistoryCalendarDay(
        DateTime date,
        bool isCurrentMonth,
        bool isToday,
        bool isSelected,
        bool isInRange)
    {
        Date = date;
        DayText = date.Day.ToString(CultureInfo.CurrentCulture);
        AccessibleDateText = LocalizationService.Current.IsEnglish
            ? date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)
            : date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture);
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        IsInRange = isInRange;
    }

    public DateTime Date { get; }

    public string DayText { get; }

    public string AccessibleDateText { get; }

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    public bool IsSelected { get; }

    public bool IsInRange { get; }
}
