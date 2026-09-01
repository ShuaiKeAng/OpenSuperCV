using System.Globalization;

namespace SuperCV;

internal static class RelativeTimeFormatter
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromHours(24);

    public static string Format(
        DateTimeOffset capturedAtUtc,
        DateTimeOffset nowUtc)
    {
        TimeSpan elapsed = nowUtc.ToUniversalTime() - capturedAtUtc.ToUniversalTime();
        if (elapsed <= TimeSpan.Zero || elapsed < OneMinute)
        {
            return LocalizationService.Current.T("刚刚");
        }

        if (elapsed < OneHour)
        {
            return LocalizationService.Current.T($"{(int)elapsed.TotalMinutes}分钟前");
        }

        if (elapsed <= OneDay)
        {
            return LocalizationService.Current.T($"{(int)elapsed.TotalHours}小时前");
        }

        return capturedAtUtc
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }
}
