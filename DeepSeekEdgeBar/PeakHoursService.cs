using System;

namespace DeepSeekEdgeBar;

public static class PeakHoursService
{
    // Peak windows (all UTC): 01:00-04:00 and 06:00-10:00 on Mon-Fri only.
    public static bool IsPeakTime(DateTime utcNow)
    {
        if (utcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = utcNow.TimeOfDay;
        return (t >= TimeSpan.FromHours(1) && t < TimeSpan.FromHours(4))
            || (t >= TimeSpan.FromHours(6) && t < TimeSpan.FromHours(10));
    }

    public static string GetStatusText(DateTime utcNow)
        => IsPeakTime(utcNow) ? "PEAK" : "OFF-PEAK";

    // Time until the next peak/off-peak transition.
    public static TimeSpan TimeUntilNextChange(DateTime utcNow)
    {
        var day = utcNow.Date;
        var nowSpan = utcNow.TimeOfDay;
        for (int dayOffset = 0; dayOffset <= 8; dayOffset++)
        {
            var d = day.AddDays(dayOffset);
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var windows = new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(6) };
            for (int w = 0; w < windows.Length; w++)
            {
                var start = windows[w];
                var end = start + (w == 0 ? TimeSpan.FromHours(3) : TimeSpan.FromHours(4));
                if (dayOffset == 0 && nowSpan < start) return start - nowSpan;
                if (dayOffset == 0 && nowSpan >= start && nowSpan < end) return end - nowSpan;
                if (dayOffset > 0) return (d + start).Date.Add(start) - utcNow;
            }
        }
        return TimeSpan.FromHours(1);
    }

    public static string FormatCountdown(TimeSpan ts)
        => $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
}