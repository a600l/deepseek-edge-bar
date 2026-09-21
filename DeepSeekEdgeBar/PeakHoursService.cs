using System;

namespace DeepSeekEdgeBar;

/// <summary>
/// DeepSeek's peak/off-peak ("峰谷") pricing windows. Off-peak is billed at half the
/// peak rate, so this drives the bar's colour, its height, and the countdown.
/// </summary>
/// <remarks>
/// Windows are <b>Beijing time (UTC+8)</b>, Mon–Fri 09:00–12:00 and 14:00–18:00. Weekends
/// and Chinese statutory holidays are off-peak all day (see <see cref="MarketCalendar"/>).
/// The published UTC equivalents are 01:00–04:00 and 06:00–10:00; we convert instead of
/// hardcoding them so the rule stays readable and the holiday lookup lands on the right
/// calendar date.
/// </remarks>
public static class PeakHoursService
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    // Boundaries of a working day, Beijing time. Every peak/off-peak flip happens at one
    // of these, so the countdown is just "the next one of these in the future".
    private static readonly TimeSpan[] WorkingDayBoundaries =
    {
        TimeSpan.FromHours(9),   // off-peak -> peak
        TimeSpan.FromHours(12),  // peak -> off-peak
        TimeSpan.FromHours(14),  // off-peak -> peak
        TimeSpan.FromHours(18),  // peak -> off-peak
    };

    /// <summary>Converts a UTC instant to Beijing wall-clock time.</summary>
    public static DateTime ToBeijing(DateTime utc) => utc + BeijingOffset;

    /// <summary>
    /// True when DeepSeek is billing peak rates. <paramref name="utcNow"/> is a UTC instant.
    /// </summary>
    public static bool IsPeakTime(DateTime utcNow)
    {
        DateTime beijing = ToBeijing(utcNow);
        if (!IsWorkingDay(beijing.Date)) return false;
        TimeSpan t = beijing.TimeOfDay;
        return (t >= WorkingDayBoundaries[0] && t < WorkingDayBoundaries[1])
            || (t >= WorkingDayBoundaries[2] && t < WorkingDayBoundaries[3]);
    }

    /// <summary>
    /// A weekday that is not a statutory holiday. Make-up workdays (调休) deliberately
    /// count as weekends: DeepSeek prices by the administrative calendar, not by which
    /// days people actually work.
    /// </summary>
    public static bool IsWorkingDay(DateTime beijingDate)
        => beijingDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
           && !MarketCalendar.IsHoliday(DateOnly.FromDateTime(beijingDate));

    public static string GetStatusText(DateTime utcNow)
        => IsPeakTime(utcNow) ? "PEAK" : "OFF-PEAK";

    /// <summary>
    /// Time until the next peak/off-peak flip: the earliest working-day boundary still in
    /// the future. Skips whole weekends and holidays in one step, so a Friday evening
    /// correctly reports the wait through to Monday 09:00.
    /// </summary>
    public static TimeSpan TimeUntilNextChange(DateTime utcNow)
    {
        DateTime beijingNow = ToBeijing(utcNow);
        // 21 days comfortably covers the longest holiday (Spring Festival, 9 days) plus
        // the weekend needed to reach the next working day.
        for (int dayOffset = 0; dayOffset <= 21; dayOffset++)
        {
            DateTime day = beijingNow.Date.AddDays(dayOffset);
            if (!IsWorkingDay(day)) continue;
            foreach (TimeSpan boundary in WorkingDayBoundaries)
            {
                DateTime instant = day + boundary;
                if (instant > beijingNow) return instant - beijingNow;
            }
        }
        return TimeSpan.FromHours(1); // Unreachable: 21 days always contains a working day.
    }

    /// <summary>
    /// Compact countdown. Days are shown once the wait crosses 24h — otherwise a long
    /// weekend reads as an absurd "62:30:00".
    /// </summary>
    public static string FormatCountdown(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours:00}:{ts.Minutes:00}";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
