using System;
using System.Collections.Generic;

namespace DeepSeekEdgeBar;

/// <summary>
/// Chinese statutory holidays, which DeepSeek bills at off-peak rates all day.
/// </summary>
/// <remarks>
/// Only <b>weekday</b> holiday dates are listed. Weekends are already off-peak under
/// DeepSeek's rule, and that includes 调休 make-up workdays — DeepSeek prices by the
/// administrative calendar, so a Saturday you actually work is still billed as a
/// weekend. Listing those here would change nothing.
///
/// Source: 国务院办公厅关于2026年部分节假日安排的通知 (国办发明电〔2025〕7号), published
/// 2025-11-04 on gov.cn. This is a hand-maintained table: add next year's dates when the
/// State Council publishes them, and bump <see cref="LastCoveredYear"/>.
/// </remarks>
public static class MarketCalendar
{
    /// <summary>Last year for which holiday dates are known.</summary>
    public const int LastCoveredYear = 2026;

    /// <summary>First year for which holiday dates are known.</summary>
    public const int FirstCoveredYear = 2026;

    private static readonly HashSet<DateOnly> ObservedHolidays = new()
    {
        // 元旦 New Year — Jan 1 (Thu) to Jan 3 (Sat); Jan 4 (Sun) was a make-up workday.
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 2),

        // 春节 Spring Festival — Feb 15 (Sun) to Feb 23 (Mon), the longest on record.
        // Feb 14 and Feb 28 (both Saturdays) were make-up workdays.
        new DateOnly(2026, 2, 16),
        new DateOnly(2026, 2, 17),
        new DateOnly(2026, 2, 18),
        new DateOnly(2026, 2, 19),
        new DateOnly(2026, 2, 20),
        new DateOnly(2026, 2, 23),

        // 清明节 Qingming — Apr 4 (Sat) to Apr 6 (Mon).
        new DateOnly(2026, 4, 6),

        // 劳动节 Labour Day — May 1 (Fri) to May 5 (Tue); May 9 (Sat) was a make-up workday.
        new DateOnly(2026, 5, 1),
        new DateOnly(2026, 5, 4),
        new DateOnly(2026, 5, 5),

        // 端午节 Dragon Boat — Jun 19 (Fri) to Jun 21 (Sun).
        new DateOnly(2026, 6, 19),

        // 中秋节 Mid-Autumn — Sep 25 (Fri) to Sep 27 (Sun).
        new DateOnly(2026, 9, 25),

        // 国庆节 National Day — Oct 1 (Thu) to Oct 7 (Wed).
        // Sep 20 (Sun) and Oct 10 (Sat) were make-up workdays.
        new DateOnly(2026, 10, 1),
        new DateOnly(2026, 10, 2),
        new DateOnly(2026, 10, 5),
        new DateOnly(2026, 10, 6),
        new DateOnly(2026, 10, 7),
    };

    /// <summary>True when the given Beijing-time date is a Chinese statutory holiday.</summary>
    public static bool IsHoliday(DateOnly beijingDate) => ObservedHolidays.Contains(beijingDate);

    /// <summary>
    /// False once the clock passes the years this table covers, so callers can warn that
    /// holiday pricing is no longer being applied rather than silently drifting.
    /// </summary>
    public static bool Covers(int year) => year is >= FirstCoveredYear and <= LastCoveredYear;
}
