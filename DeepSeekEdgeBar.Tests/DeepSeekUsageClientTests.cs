using System;
using System.Globalization;
using System.Net.Http;
using DeepSeekEdgeBar;

namespace DeepSeekEdgeBar.Tests;

/// <summary>
/// Locks in the usage parser against the platform's *legacy* envelope, which is what the shipped
/// client actually calls.
///
/// This file exists because the parser is the one place in this app where being wrong is silent.
/// A misread response does not throw for the current-month case — it just yields numbers nobody
/// can sanity-check at a glance, which is how a stale day was once relabelled as TODAY (see
/// commit ca6a4e5 and <see cref="AStaleDayIsNeverRelabelledAsToday"/>).
///
/// The fixtures below mirror bodies captured live on 2026-09-22, with two properties worth
/// preserving deliberately:
///   - the amount endpoint wraps one object in `biz_data`, the cost endpoint wraps a 1-element
///     ARRAY (CLAUDE.md documents this, and it is easy to "clean up" by accident);
///   - day buckets keyed by date include days with all-zero rows, including future dates.
///
/// Dates are computed from DateTime.Today rather than hardcoded, so these tests do not rot.
/// </summary>
public class DeepSeekUsageClientTests
{
    private static readonly string Yesterday =
        DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static readonly string Today =
        DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static readonly string Tomorrow =
        DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // One model row inside a day bucket or a `total` row.
    private static string Tokens(string model, long cacheHit, long cacheMiss, long response, long requests) => $$"""
        {
          "model": "{{model}}",
          "usage": [
            { "type": "PROMPT_TOKEN", "amount": "0" },
            { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "{{cacheHit}}" },
            { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "{{cacheMiss}}" },
            { "type": "RESPONSE_TOKEN", "amount": "{{response}}" },
            { "type": "REQUEST", "amount": "{{requests}}" }
          ]
        }
        """;

    private static string CostRow(string model, string cost) => $$"""
        {
          "model": "{{model}}",
          "usage": [
            { "type": "PROMPT_TOKEN", "amount": "0" },
            { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "{{cost}}" },
            { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "0" },
            { "type": "RESPONSE_TOKEN", "amount": "0" },
            { "type": "REQUEST", "amount": "0" }
          ]
        }
        """;

    /// <summary>
    /// An amount body: yesterday 1,250 tok / 10 req, today 7,500 tok / 28 req, plus an all-zero
    /// future day. Month totals are the sum of both days.
    /// </summary>
    private static string AmountBody() => $$"""
        {
          "code": 0,
          "msg": "",
          "data": {
            "biz_code": 0,
            "biz_msg": "",
            "biz_data": {
              "total": [
                {{Tokens("deepseek-flash", 6000, 600, 150, 35)}},
                {{Tokens("deepseek-v4-flash", 2000, 0, 0, 3)}}
              ],
              "days": [
                { "date": "{{Yesterday}}", "data": [ {{Tokens("deepseek-flash", 1000, 200, 50, 10)}} ] },
                { "date": "{{Today}}", "data": [
                    {{Tokens("deepseek-flash", 5000, 400, 100, 25)}},
                    {{Tokens("deepseek-v4-flash", 2000, 0, 0, 3)}}
                ] },
                { "date": "{{Tomorrow}}", "data": [ {{Tokens("deepseek-flash", 0, 0, 0, 0)}} ] }
              ]
            }
          }
        }
        """;

    /// <summary>The cost envelope: `biz_data` is a 1-element array, not an object.</summary>
    private static string CostBody() => $$"""
        {
          "code": 0,
          "msg": "",
          "data": {
            "biz_code": 0,
            "biz_msg": "",
            "biz_data": [
              {
                "currency": "USD",
                "total": [
                  {{CostRow("deepseek-flash", "4.5000000000000000")}},
                  {{CostRow("deepseek-v4-flash", "0.1000000000000000")}}
                ],
                "days": [
                  { "date": "{{Yesterday}}", "data": [ {{CostRow("deepseek-flash", "1.4000000000000000")}} ] },
                  { "date": "{{Today}}", "data": [
                      {{CostRow("deepseek-flash", "3.0000000000000000")}},
                      {{CostRow("deepseek-v4-flash", "0.1000000000000000")}}
                  ] }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>A month with no usage at all, as the platform answers for a future month.</summary>
    private const string EmptyBizDataBody = """
        { "code": 0, "msg": "", "data": { "biz_code": 0, "biz_msg": "", "biz_data": null } }
        """;

    private static UsageSnapshot Parse() => DeepSeekUsageClient.BuildSnapshot(AmountBody(), CostBody());

    [Fact]
    public void TodaysRowSumsOnlyTodaysBucket()
    {
        UsageSnapshot s = Parse();

        // 5000 + 400 + 100 + 2000; PROMPT_TOKEN is always 0 and must not disturb the sum.
        Assert.Equal(7500, s.TodayTokens);
        Assert.Equal(28, s.TodayRequests);   // 25 + 3 — REQUEST is a count, never a token total
        Assert.Equal(3.1, s.TodayCost, 6);
    }

    [Fact]
    public void MonthTotalsComeFromTheTotalRows()
    {
        UsageSnapshot s = Parse();

        Assert.Equal(8750, s.MonthTokens);
        Assert.Equal(38, s.MonthRequests);
        Assert.Equal(4.6, s.MonthCost, 6);
    }

    /// <summary>
    /// The regression that motivated this whole file: with no bucket for today at all, the row must
    /// read zero and point at the last real day — never borrow a neighbouring day's numbers and
    /// label them TODAY.
    ///
    /// Note the fixture deliberately omits today's bucket entirely rather than filling it with
    /// zeros. A zero-filled bucket would satisfy the exact-date lookup and never reach the
    /// fallback this test exists to police — verified by reintroducing the ca6a4e5 bug and
    /// watching this test fail.
    /// </summary>
    [Fact]
    public void AStaleDayIsNeverRelabelledAsToday()
    {
        string amount = $$"""
            {
              "code": 0, "msg": "",
              "data": { "biz_code": 0, "biz_msg": "", "biz_data": {
                "total": [ {{Tokens("deepseek-flash", 6000, 600, 150, 35)}} ],
                "days": [
                  { "date": "{{Yesterday}}", "data": [ {{Tokens("deepseek-flash", 1000, 200, 50, 10)}} ] },
                  { "date": "{{Tomorrow}}", "data": [ {{Tokens("deepseek-flash", 0, 0, 0, 0)}} ] }
                ]
              } }
            }
            """;
        string cost = $$"""
            {
              "code": 0, "msg": "",
              "data": { "biz_code": 0, "biz_msg": "", "biz_data": [ { "currency": "USD",
                "total": [ {{CostRow("deepseek-flash", "4.5")}} ],
                "days": [ { "date": "{{Yesterday}}", "data": [ {{CostRow("deepseek-flash", "1.4")}} ] } ]
              } ] }
            }
            """;

        UsageSnapshot s = DeepSeekUsageClient.BuildSnapshot(amount, cost);

        // If these ever read 1250 / 10, the stale bucket is being relabelled as today.
        Assert.Equal(0, s.TodayTokens);
        Assert.Equal(0, s.TodayRequests);
        Assert.Equal(0d, s.TodayCost, 6);
        // Not today, and not the trailing zero day either.
        Assert.Equal(Yesterday, s.LastActiveDate);
    }

    /// <summary>The other half: today's bucket present but all-zero is a real idle day, not staleness.</summary>
    [Fact]
    public void AZeroFilledTodayBucketReadsAsAnIdleDay()
    {
        string amount = $$"""
            {
              "code": 0, "msg": "",
              "data": { "biz_code": 0, "biz_msg": "", "biz_data": {
                "total": [ {{Tokens("deepseek-flash", 6000, 600, 150, 35)}} ],
                "days": [
                  { "date": "{{Yesterday}}", "data": [ {{Tokens("deepseek-flash", 1000, 200, 50, 10)}} ] },
                  { "date": "{{Today}}", "data": [ {{Tokens("deepseek-flash", 0, 0, 0, 0)}} ] }
                ]
              } }
            }
            """;

        UsageSnapshot s = DeepSeekUsageClient.BuildSnapshot(amount, amount);

        Assert.Equal(0, s.TodayTokens);
        Assert.Equal(0, s.TodayRequests);
        // Today is still the most recent *bucket*, but it holds nothing, so the last real day wins.
        Assert.Equal(Yesterday, s.LastActiveDate);
    }

    [Fact]
    public void PerModelTotalsAndTopModelFollowUsage()
    {
        UsageSnapshot s = Parse();

        Assert.Equal(2, s.PerModel.Count);
        Assert.Equal("deepseek-flash", s.PerModel[0].Model);
        Assert.Equal(6750, s.PerModel[0].Tokens);
        Assert.Equal(35, s.PerModel[0].Requests);
        Assert.Equal(4.5, s.PerModel[0].Cost, 6);
        Assert.Equal("deepseek-v4-flash", s.PerModel[1].Model);
        Assert.Equal(2000, s.PerModel[1].Tokens);

        // Sorted by tokens descending, and TopModel is the leader.
        Assert.True(s.PerModel[0].Tokens >= s.PerModel[1].Tokens);
        Assert.Equal("deepseek-flash", s.TopModel);
    }

    [Fact]
    public void AnEmptyMonthReadsAsUnavailableRatherThanZero()
    {
        // A month the platform has no data for answers with biz_data:null. The parser must not
        // dress that up as a real "0 tokens" reading.
        Assert.Throws<HttpRequestException>(
            () => DeepSeekUsageClient.BuildSnapshot(EmptyBizDataBody, EmptyBizDataBody));
    }

    [Fact]
    public void AnExpiredTokenRaisesTheAuthExceptionTheUiCatches()
    {
        // MainWindow catches DeepSeekUsageAuthException separately to say "token expired";
        // it must not arrive as a generic failure.
        const string expired = """
            { "code": 40003, "msg": "invalid token", "data": null }
            """;

        Assert.Throws<DeepSeekUsageAuthException>(
            () => DeepSeekUsageClient.BuildSnapshot(expired, expired));
    }

    [Fact]
    public void OtherErrorCodesRaiseAGenericFailure()
    {
        const string other = """
            { "code": 50001, "msg": "boom", "data": null }
            """;

        Assert.Throws<HttpRequestException>(
            () => DeepSeekUsageClient.BuildSnapshot(other, other));
    }

    [Fact]
    public void MissingTopLevelCodeIsTreatedAsAnError()
    {
        const string noCode = """
            { "msg": "no code here", "data": { "biz_code": 0, "biz_msg": "", "biz_data": { "total": [], "days": [] } } }
            """;

        Assert.Throws<HttpRequestException>(
            () => DeepSeekUsageClient.BuildSnapshot(noCode, noCode));
    }
}
