using System;
using DeepSeekEdgeBar;

namespace DeepSeekEdgeBar.Tests;

/// <summary>
/// Locks in DeepSeek's peak/off-peak rule. This logic is silent when wrong — the bar just
/// displays the wrong price tier — so every boundary and every holiday is pinned here.
/// </summary>
public class PeakHoursServiceTests
{
    /// <summary>A UTC instant, written as the UTC wall clock.</summary>
    private static DateTime Utc(int y, int m, int d, int hh, int mm = 0, int ss = 0)
        => new(y, m, d, hh, mm, ss, DateTimeKind.Utc);

    /// <summary>A UTC instant, written as the Beijing wall clock it corresponds to.</summary>
    private static DateTime Beijing(int y, int m, int d, int hh, int mm = 0, int ss = 0)
        => new DateTime(y, m, d, hh, mm, ss, DateTimeKind.Utc).AddHours(-8);

    // 2026-09-21 is a Monday and not a holiday.
    private const int Mon = 21, Tue = 22, Wed = 23, Thu = 24, Fri = 25, Sat = 26, Sun = 27;

    [Theory]
    // Working-day peak windows, in Beijing time: 09:00-12:00 and 14:00-18:00.
    [InlineData(9, 0, true)]
    [InlineData(10, 30, true)]
    [InlineData(11, 59, true)]
    [InlineData(12, 0, false)]
    [InlineData(13, 0, false)]
    [InlineData(14, 0, true)]
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)]
    [InlineData(19, 0, false)]
    [InlineData(8, 59, false)]
    [InlineData(0, 0, false)]
    public void PeakWindowsFollowBeijingWorkingHours(int hour, int minute, bool expected)
        => Assert.Equal(expected, PeakHoursService.IsPeakTime(Beijing(2026, 9, Mon, hour, minute)));

    [Theory]
    [InlineData(Sat)]
    [InlineData(Sun)]
    public void WeekendsAreOffPeakAllDay(int day)
    {
        // 10:00 Beijing would be peak on a weekday.
        Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 9, day, 10, 0)));
        Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 9, day, 15, 0)));
    }

    [Fact]
    public void MidAutumnHolidayIsOffPeakOnAWeekday()
    {
        // Fri 2026-09-25 is 中秋节 — a weekday the State Council made a holiday, so
        // DeepSeek bills it off-peak.
        Assert.Equal(DayOfWeek.Friday, new DateTime(2026, 9, 25).DayOfWeek);
        Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 9, 25, 10, 0)));
        // The Thursday before it is an ordinary working day, and is peak.
        Assert.True(PeakHoursService.IsPeakTime(Beijing(2026, 9, Thu, 10, 0)));
    }

    [Theory]
    [InlineData(1)]  // Thu — 国庆节 National Day
    [InlineData(2)]  // Fri
    [InlineData(5)]  // Mon
    [InlineData(6)]  // Tue
    [InlineData(7)]  // Wed
    public void NationalDayWeekIsOffPeak(int day)
        => Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 10, day, 10, 0)));

    [Fact]
    public void WorkResumesAfterNationalDay()
        => Assert.True(PeakHoursService.IsPeakTime(Beijing(2026, 10, 8, 10, 0)));

    [Fact]
    public void SpringFestivalIsOffPeakAcrossItsWholeRun()
    {
        // Feb 15 (Sun) to Feb 23 (Mon) 2026.
        for (int day = 15; day <= 23; day++)
            Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 2, day, 10, 0)));
        Assert.True(PeakHoursService.IsPeakTime(Beijing(2026, 2, 24, 10, 0)));
    }

    [Fact]
    public void MakeUpWorkdayIsStillBilledAsAWeekend()
    {
        // Sun 2026-09-20 was designated a make-up workday (调休), but DeepSeek prices by
        // the weekend rule, so it stays off-peak.
        Assert.Equal(DayOfWeek.Sunday, new DateTime(2026, 9, 20).DayOfWeek);
        Assert.False(PeakHoursService.IsPeakTime(Beijing(2026, 9, 20, 10, 0)));
    }

    [Fact]
    public void ToBeijingShiftsByEightHours()
        => Assert.Equal(new DateTime(2026, 9, Mon, 10, 0, 0), PeakHoursService.ToBeijing(Utc(2026, 9, Mon, 2, 0)));

    // --- TimeUntilNextChange -------------------------------------------------------------

    [Fact]
    public void CountdownFromMidPeakLandsOnPeakEnd()
        => Assert.Equal(TimeSpan.FromHours(2), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, Mon, 10, 0)));

    [Fact]
    public void CountdownFromTheOffPeakLullLandsOnTheAfternoonPeak()
        => Assert.Equal(TimeSpan.FromHours(1), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, Mon, 13, 0)));

    [Fact]
    public void CountdownAfterTheLastWindowLandsOnTomorrowMorning()
        => Assert.Equal(TimeSpan.FromHours(14), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, Mon, 19, 0)));

    [Fact]
    public void CountdownSkipsTheWeekend()
    {
        // Fri 2026-09-18 19:00 -> Mon 2026-09-21 09:00 is 2 days 14 hours.
        Assert.Equal(DayOfWeek.Friday, new DateTime(2026, 9, 18).DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(62), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, 18, 19, 0)));
    }

    [Fact]
    public void CountdownSkipsTheMidAutumnHoliday()
    {
        // Thu 2026-09-24 19:00 -> Mon 2026-09-28 09:00 is 3 days 14 hours, because
        // Sep 25 is a holiday and Sep 26-27 is a weekend.
        Assert.Equal(TimeSpan.FromHours(86), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, Thu, 19, 0)));
    }

    [Fact]
    public void CountdownSkipsTheWholeNationalDayWeek()
    {
        // Wed 2026-09-30 19:00 -> Thu 2026-10-08 09:00 is 7 days 14 hours.
        Assert.Equal(TimeSpan.FromHours(182), PeakHoursService.TimeUntilNextChange(Beijing(2026, 9, 30, 19, 0)));
    }

    [Fact]
    public void EveryTransitionFlipsThePeakState()
    {
        // Walk the working day a minute at a time and check that IsPeakTime only ever
        // changes at a moment we would have counted down to.
        var previous = PeakHoursService.IsPeakTime(Beijing(2026, 9, Mon, 0, 0));
        for (int minute = 1; minute < 24 * 60; minute++)
        {
            DateTime now = Beijing(2026, 9, Mon, 0, 0).AddMinutes(minute);
            bool current = PeakHoursService.IsPeakTime(now);
            if (current != previous)
            {
                TimeSpan before = PeakHoursService.TimeUntilNextChange(now.AddMinutes(-1));
                Assert.Equal(TimeSpan.FromMinutes(1), before);
                previous = current;
            }
        }
    }

    // --- FormatCountdown -----------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 45, "00:45")]
    [InlineData(0, 5, 0, "05:00")]
    [InlineData(1, 0, 0, "01:00:00")]
    [InlineData(2, 0, 0, "02:00:00")]
    [InlineData(23, 59, 59, "23:59:59")]
    [InlineData(26, 0, 0, "1d 02:00")]
    [InlineData(62, 0, 0, "2d 14:00")]
    public void CountdownFormattingStaysCompact(int hours, int minutes, int seconds, string expected)
        => Assert.Equal(expected, PeakHoursService.FormatCountdown(
            new TimeSpan(hours, minutes, seconds)));

    [Fact]
    public void NegativeCountdownClampsToZero()
        => Assert.Equal("00:00", PeakHoursService.FormatCountdown(TimeSpan.FromSeconds(-5)));
}

/// <summary>
/// Guards the hand-maintained holiday table against typos: a wrong date is invisible at
/// runtime, so the table's own invariants are asserted here.
/// </summary>
public class MarketCalendarTests
{
    [Fact]
    public void EveryHolidayIsAWeekday()
    {
        // Weekends are already off-peak, so a weekend entry means someone mistyped a date.
        for (int year = MarketCalendar.FirstCoveredYear; year <= MarketCalendar.LastCoveredYear; year++)
            for (int month = 1; month <= 12; month++)
                for (int day = 1; day <= DateTime.DaysInMonth(year, month); day++)
                {
                    var date = new DateOnly(year, month, day);
                    if (!MarketCalendar.IsHoliday(date)) continue;
                    Assert.False(
                        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                        $"{date:yyyy-MM-dd} is a weekend and should not be listed as a holiday");
                }
    }

    [Fact]
    public void KnownHolidaysArePresent()
    {
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 1, 1)));    // 元旦
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 2, 17)));   // 春节
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 4, 6)));    // 清明节
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 5, 1)));    // 劳动节
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 6, 19)));   // 端午节
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 9, 25)));   // 中秋节
        Assert.True(MarketCalendar.IsHoliday(new DateOnly(2026, 10, 1)));   // 国庆节
    }

    [Fact]
    public void OrdinaryWorkingDaysAreNotHolidays()
    {
        Assert.False(MarketCalendar.IsHoliday(new DateOnly(2026, 9, 21)));
        Assert.False(MarketCalendar.IsHoliday(new DateOnly(2026, 10, 8)));
        Assert.False(MarketCalendar.IsHoliday(new DateOnly(2026, 12, 25)));
    }

    [Fact]
    public void CoverageReflectsTheTable()
    {
        Assert.True(MarketCalendar.Covers(2026));
        // Once the clock rolls past the table, callers must fall back to weekends-only
        // and say so rather than silently pricing holidays as peak.
        Assert.False(MarketCalendar.Covers(2027));
        Assert.False(MarketCalendar.Covers(2025));
    }
}
