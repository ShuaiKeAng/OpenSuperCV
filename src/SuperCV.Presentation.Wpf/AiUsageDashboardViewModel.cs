using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SuperCV.Infrastructure.Windows;

namespace SuperCV;

/// <summary>Presentation-only projection of provider-reported token usage for the Settings dashboard.</summary>
public sealed class AiUsageDashboardViewModel : INotifyPropertyChanged
{
    private readonly AiTokenUsageLedger _ledger;

    public AiUsageDashboardViewModel(AiTokenUsageLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        RecentDays = new ObservableCollection<AiUsageDayViewModel>();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AiUsageDayViewModel> RecentDays { get; }

    public string TotalTokensDisplay { get; private set; } = "0";

    public string PromptTokensDisplay { get; private set; } = "0";

    public string CompletionTokensDisplay { get; private set; } = "0";

    public string ReasoningTokensDisplay { get; private set; } = "0";

    public string RequestCountDisplay { get; private set; } = "0";

    public string CacheTokensDisplay { get; private set; } = "0";

    public string LastUpdatedDisplay { get; private set; } = "等待模型调用";

    public string EmptyStateVisibilityText { get; private set; } = "服务端尚未返回 usage 数据";

    public bool HasUsage { get; private set; }

    public void Refresh()
    {
        IReadOnlyList<AiTokenUsageEntry> entries = _ledger.Snapshot;
        int total = entries.Sum(entry => entry.Usage.TotalTokens);
        int prompt = entries.Sum(entry => entry.Usage.PromptTokens);
        int completion = entries.Sum(entry => entry.Usage.CompletionTokens);
        int reasoning = entries.Sum(entry => entry.Usage.ReasoningTokens);
        int cached = entries.Sum(entry => entry.Usage.CachedTokens);
        HasUsage = entries.Count > 0;
        TotalTokensDisplay = FormatTokens(total);
        PromptTokensDisplay = FormatTokens(prompt);
        CompletionTokensDisplay = FormatTokens(completion);
        ReasoningTokensDisplay = FormatTokens(reasoning);
        CacheTokensDisplay = FormatTokens(cached);
        RequestCountDisplay = entries.Count.ToString("N0", CultureInfo.InvariantCulture);
        LastUpdatedDisplay = entries.Count == 0
            ? "等待模型调用"
            : "最近更新 " + entries.Max(entry => entry.RecordedAt).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var days = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(offset - 6))
            .Select(day => new
            {
                Day = day,
                Usage = entries.Where(entry => DateOnly.FromDateTime(entry.RecordedAt.LocalDateTime) == day)
                    .Sum(entry => entry.Usage.TotalTokens),
            })
            .ToArray();
        int peak = Math.Max(1, days.Max(day => day.Usage));
        RecentDays.Clear();
        foreach (var day in days)
        {
            RecentDays.Add(new AiUsageDayViewModel(
                day.Day.ToString("MM/dd", CultureInfo.InvariantCulture),
                day.Usage,
                day.Usage == 0 ? 4 : Math.Max(8, (int)Math.Round(54d * day.Usage / peak))));
        }

        foreach (string propertyName in new[]
                 {
                     nameof(TotalTokensDisplay), nameof(PromptTokensDisplay), nameof(CompletionTokensDisplay),
                     nameof(ReasoningTokensDisplay), nameof(CacheTokensDisplay), nameof(RequestCountDisplay),
                     nameof(LastUpdatedDisplay), nameof(HasUsage),
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }

    private static string FormatTokens(int value) => value >= 1_000
        ? (value / 1_000d).ToString(value >= 10_000 ? "0.0K" : "0.00K", CultureInfo.InvariantCulture)
        : value.ToString("N0", CultureInfo.InvariantCulture);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record AiUsageDayViewModel(string DayLabel, int TotalTokens, int BarHeight)
{
    public string Tooltip => $"{DayLabel} · {TotalTokens:N0} tokens";
}
