using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekEdgeBar;

public record BalanceInfo(
    bool IsAvailable,
    string Currency,
    decimal TotalBalance,
    decimal GrantedBalance,
    decimal ToppedUpBalance);

public class DeepSeekApiClient
{
    private static readonly HttpClient Shared = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _apiKey;

    public DeepSeekApiClient(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key not configured. Add your DeepSeek API key in Settings.");
        _apiKey = apiKey;
    }

    public async Task<BalanceInfo> GetBalanceAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await Shared.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek API error {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        bool available = root.TryGetProperty("is_available", out var a) && a.GetBoolean();
        string currency = "USD";
        decimal total = 0, granted = 0, topped = 0;
        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array && infos.GetArrayLength() > 0)
        {
            var b = infos[0];
            if (b.TryGetProperty("currency", out var c)) currency = c.GetString() ?? "USD";
            if (b.TryGetProperty("total_balance", out var t)) decimal.TryParse(t.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out total);
            if (b.TryGetProperty("granted_balance", out var g)) decimal.TryParse(g.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out granted);
            if (b.TryGetProperty("topped_up_balance", out var tp)) decimal.TryParse(tp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out topped);
        }
        return new BalanceInfo(available, currency, total, granted, topped);
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await Shared.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek API error {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id))
                    list.Add(id.GetString() ?? "");
        return list;
    }
}