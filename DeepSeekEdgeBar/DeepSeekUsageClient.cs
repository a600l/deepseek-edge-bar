using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekEdgeBar;

public record ModelUsage(string Model, long Tokens, long Requests, double Cost);

public record UsageSnapshot(
    bool IsAvailable,
    string? Error,
    long TodayTokens,
    long TodayRequests,
    double TodayCost,
    long MonthTokens,
    long MonthRequests,
    double MonthCost,
    string? TopModel,
    List<ModelUsage> PerModel,
    // ISO date of the most recent day that recorded usage, or null. Lets the panel say how
    // stale the last real numbers are rather than presenting them as today's.
    string? LastActiveDate);

public class DeepSeekUsageAuthException : Exception
{
    public DeepSeekUsageAuthException(string message) : base(message) { }
}

public class DeepSeekUsageClient
{
    private static readonly HttpClient Shared = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private const string BaseUrl = "https://platform.deepseek.com/api/v0";

    public async Task<UsageSnapshot> GetUsageAsync(string platformToken, CancellationToken ct = default)
    {
        string token = (platformToken ?? "").Trim();
        if (token.Length == 0)
            throw new DeepSeekUsageAuthException("Platform token not configured.");
        DateTime local = DateTime.Now;
        string query = $"?month={local.Month}&year={local.Year}";
        string amountBody = await GetAsync(BaseUrl + "/usage/amount" + query, token, ct).ConfigureAwait(false);
        string costBody = await GetAsync(BaseUrl + "/usage/cost" + query, token, ct).ConfigureAwait(false);
        return BuildSnapshot(amountBody, costBody);
    }

    private async Task<string> GetAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var resp = await Shared.SendAsync(req, ct).ConfigureAwait(false);
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Usage data timed out — try again later.");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("Could not reach the DeepSeek platform — check your connection.", ex);
        }
    }

    /// <summary>
    /// Parses one amount body and one cost body into a snapshot. Internal rather than private so
    /// the tests can drive it from recorded JSON; it does no I/O and reads nothing else.
    /// </summary>
    internal static UsageSnapshot BuildSnapshot(string amountBody, string costBody)
    {
        JsonElement amountBiz = ParseBizData(amountBody);
        JsonElement costBiz = ParseBizData(costBody);

        List<JsonElement> totalAmount = RowsOf(amountBiz, "total");
        List<JsonElement> daysAmount = RowsOf(amountBiz, "days");
        List<JsonElement> totalCost = RowsOf(costBiz, "total");
        List<JsonElement> daysCost = RowsOf(costBiz, "days");

        long monthTokens, monthRequests;
        SumRows(totalAmount, out monthTokens, out monthRequests);
        double monthCost = CostOfRows(totalCost);

        List<JsonElement> todayAmountRows = RowsForExactDate(daysAmount, DateTime.Today);
        List<JsonElement> todayCostRows = RowsForExactDate(daysCost, DateTime.Today);

        long todayTokens, todayRequests;
        SumRows(todayAmountRows, out todayTokens, out todayRequests);
        double todayCost = CostOfRows(todayCostRows);

        List<ModelUsage> perModel = AggregatePerModel(totalAmount, totalCost);
        string? topModel = perModel.Count > 0 ? perModel[0].Model : null;

        return new UsageSnapshot(true, null, todayTokens, todayRequests, todayCost, monthTokens, monthRequests, monthCost, topModel, perModel, MostRecentActiveDate(daysAmount));
    }

    private static JsonElement ParseBizData(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        if (TryGetCode(root, out int code))
        {
            if (code == 40003)
                throw new DeepSeekUsageAuthException("Platform token invalid or expired — refresh it in Settings.");
            if (code != 0)
                throw new HttpRequestException($"Usage data unavailable (code {code}).");
        }
        else
        {
            throw new HttpRequestException("Usage data unavailable.");
        }
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("biz_data", out var bizData))
        {
            if (bizData.ValueKind == JsonValueKind.Object) return bizData.Clone();
            if (bizData.ValueKind == JsonValueKind.Array && bizData.GetArrayLength() > 0 &&
                bizData[0].ValueKind == JsonValueKind.Object)
                return bizData[0].Clone();
        }
        throw new HttpRequestException("Usage data unavailable.");
    }

    private static bool TryGetCode(JsonElement root, out int code)
    {
        code = -1;
        if (!root.TryGetProperty("code", out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out code)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out code)) return true;
        if (el.ValueKind == JsonValueKind.True) { code = 1; return true; }
        if (el.ValueKind == JsonValueKind.False) { code = 0; return true; }
        return false;
    }

    private static List<JsonElement> RowsOf(JsonElement parent, string key)
    {
        var list = new List<JsonElement>();
        if (parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in el.EnumerateArray()) list.Add(row);
        }
        return list;
    }

    private static List<JsonElement> ModelsOfDay(JsonElement day) => RowsOf(day, "data");

    /// <summary>
    /// The models recorded under exactly this date, or nothing.
    ///
    /// Deliberately no "closest day that has data" fallback. The day buckets are keyed by the
    /// platform's own calendar, which is not necessarily this machine's — the endpoint ignores
    /// a tz parameter, so there is no way to ask for a different one. A missing bucket therefore
    /// means "no usage that day", not "date unavailable". Substituting the most recent active
    /// day silently relabelled it as TODAY; with sparse usage that number could be days stale.
    /// </summary>
    private static List<JsonElement> RowsForExactDate(List<JsonElement> days, DateTime date)
    {
        string want = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var day in days)
        {
            string d = day.TryGetProperty("date", out var el) ? el.GetString() ?? "" : "";
            if (string.Equals(d, want, StringComparison.Ordinal))
                return ModelsOfDay(day);
        }
        return new List<JsonElement>();
    }

    private static string? MostRecentActiveDate(List<JsonElement> days)
    {
        for (int i = days.Count - 1; i >= 0; i--)
        {
            if (!DayHasData(days[i])) continue;
            return days[i].TryGetProperty("date", out var el) ? el.GetString() : null;
        }
        return null;
    }

    private static bool DayHasData(JsonElement day)
    {
        foreach (var row in ModelsOfDay(day))
        {
            foreach (var usage in UsageRows(row))
                if (AmountOf(usage) != 0)
                    return true;
        }
        return false;
    }

    private static string ModelOf(JsonElement row)
        => row.TryGetProperty("model", out var el) ? el.GetString() ?? "" : "";

    private static List<JsonElement> UsageRows(JsonElement row)
    {
        var list = new List<JsonElement>();
        if (row.TryGetProperty("usage", out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in el.EnumerateArray()) list.Add(u);
        }
        return list;
    }

    private static string TypeOf(JsonElement usage)
        => usage.TryGetProperty("type", out var el) ? el.GetString() ?? "" : "TOKEN";

    private static long AmountOf(JsonElement usage)
    {
        if (!usage.TryGetProperty("amount", out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long l)) return l;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return (long)d;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)) return s;
        return 0;
    }

    private static double CostOfUsage(JsonElement usage)
    {
        if (!usage.TryGetProperty("amount", out var el)) return 0d;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
        return 0d;
    }

    private static double CostOfRows(List<JsonElement> rows)
    {
        double total = 0d;
        foreach (var row in rows)
            foreach (var usage in UsageRows(row))
                if (StringComparer.Ordinal.Equals(TypeOf(usage), "REQUEST") is false)
                    total += CostOfUsage(usage);
        return total;
    }

    private static void SumRows(List<JsonElement> rows, out long tokens, out long requests)
    {
        tokens = 0;
        requests = 0;
        foreach (var row in rows)
        {
            foreach (var usage in UsageRows(row))
            {
                if (StringComparer.Ordinal.Equals(TypeOf(usage), "REQUEST"))
                    requests += AmountOf(usage);
                else
                    tokens += AmountOf(usage);
            }
        }
    }

    private static void AddModel(List<JsonElement> rows, Dictionary<string, long> tokensByModel, Dictionary<string, long> requestsByModel)
    {
        foreach (var row in rows)
        {
            string model = ModelOf(row);
            if (model.Length == 0) continue;
            foreach (var usage in UsageRows(row))
            {
                if (StringComparer.Ordinal.Equals(TypeOf(usage), "REQUEST"))
                    requestsByModel[model] = (requestsByModel.TryGetValue(model, out long r) ? r : 0) + AmountOf(usage);
                else
                    tokensByModel[model] = (tokensByModel.TryGetValue(model, out long t) ? t : 0) + AmountOf(usage);
            }
        }
    }

    private static List<ModelUsage> AggregatePerModel(List<JsonElement> amountTotal, List<JsonElement> costTotal)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokensByModel = new Dictionary<string, long>(StringComparer.Ordinal);
        var requestsByModel = new Dictionary<string, long>(StringComparer.Ordinal);
        var costByModel = new Dictionary<string, double>(StringComparer.Ordinal);

        AddModel(amountTotal, tokensByModel, requestsByModel);
        foreach (var model in tokensByModel.Keys)
            if (seen.Add(model)) order.Add(model);
        foreach (var model in requestsByModel.Keys)
            if (seen.Add(model)) order.Add(model);
        foreach (var row in costTotal)
        {
            string model = ModelOf(row);
            if (model.Length == 0) continue;
            if (seen.Add(model)) order.Add(model);
            costByModel[model] = (costByModel.TryGetValue(model, out double c) ? c : 0d) + CostOfRows(new List<JsonElement> { row });
        }

        var result = new List<ModelUsage>(order.Count);
        foreach (string model in order)
        {
            long tokens = tokensByModel.TryGetValue(model, out long t) ? t : 0;
            long requests = requestsByModel.TryGetValue(model, out long r) ? r : 0;
            double cost = costByModel.TryGetValue(model, out double c) ? c : 0d;
            result.Add(new ModelUsage(model, tokens, requests, cost));
        }
        result.Sort((a, b) => b.Tokens.CompareTo(a.Tokens));
        return result;
    }
}