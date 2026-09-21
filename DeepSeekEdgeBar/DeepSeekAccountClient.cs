using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeekEdgeBar;

public record AccountSession(string Token, string UserId, string Email, bool IsMuted, long? MuteUntil, string Name, string Provider, string Picture);

public class DeepSeekAuthException : Exception
{
    public DeepSeekAuthException(string message) : base(message) { }
}

public class DeepSeekAccountClient
{
    private static readonly HttpClient Shared = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private const string BaseUrl = "https://chat.deepseek.com/api/v0";

    public async Task<AccountSession> LoginAsync(string email, string password)
    {
        string deviceId = SettingsStore.GetOrCreateDeviceId();
        string json = JsonSerializer.Serialize(new
        {
            email,
            mobile = (string?)null,
            password,
            area_code = (string?)null,
            device_id = deviceId,
            os = "android"
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/users/login");
        AddLoginHeaders(req);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Shared.SendAsync(req).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)resp.StatusCode == 422)
            throw new DeepSeekAuthException("Sign-in rejected — check that email and password are filled in.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek sign-in failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.TryGetInt32(out int codeVal) && codeVal != 0)
            throw new DeepSeekAuthException("Sign-in failed.");

        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("biz_code", out var bizCode) &&
                bizCode.ValueKind == JsonValueKind.Number &&
                bizCode.TryGetInt32(out int bizCodeVal) && bizCodeVal != 0)
            {
                string bizMsg = data.TryGetProperty("biz_msg", out var bm) ? bm.GetString() ?? "" : "";
                throw new DeepSeekAuthException(FriendlyMessage(bizMsg));
            }
            if (data.TryGetProperty("biz_data", out var bizData) &&
                bizData.TryGetProperty("user", out var user))
                return ParseUser(user);
        }

        throw new DeepSeekAuthException("Unexpected sign-in response.");
    }

    public async Task<AccountSession> GetCurrentAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/users/current");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddLoginHeaders(req);
        using var resp = await Shared.SendAsync(req).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)resp.StatusCode == 401)
            throw new DeepSeekAuthException("Session expired — sign in again.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek session check failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("code", out var code))
        {
            if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int codeVal) && codeVal != 0)
            {
                if (codeVal is 40001 or 40002 or 40003)
                    throw new DeepSeekAuthException("Session expired — sign in again.");
                throw new DeepSeekAuthException($"DeepSeek session error ({codeVal}).");
            }
        }

        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("biz_data", out var bizData) && bizData.ValueKind == JsonValueKind.Object)
            return ParseUser(bizData);

        throw new DeepSeekAuthException("Unexpected session response.");
    }

    public async Task<string?> CheckDeviceAsync(string token)
    {
        try
        {
            string deviceId = SettingsStore.GetOrCreateDeviceId();
            string json = JsonSerializer.Serialize(new { device_id = deviceId, device_model = "" });
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/users/auth_token/check_device");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddLoginHeaders(req);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Shared.SendAsync(req).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("biz_data", out var bizData) &&
                bizData.TryGetProperty("rotate", out var rotate) && rotate.ValueKind == JsonValueKind.String)
            {
                string rotateStr = rotate.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rotateStr))
                {
                    using var inner = JsonDocument.Parse(rotateStr);
                    if (inner.RootElement.TryGetProperty("token", out var t))
                        return t.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static AccountSession ParseUser(JsonElement user)
    {
        string token = user.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";
        string email = user.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "";
        string userId = "";
        if (user.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.String) userId = id.GetString() ?? "";
            else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long idNum)) userId = idNum.ToString();
        }
        bool isMuted = false;
        long? muteUntil = null;
        if (user.TryGetProperty("chat", out var chat))
        {
            if (chat.TryGetProperty("is_muted", out var isMutedEl))
            {
                if (isMutedEl.ValueKind == JsonValueKind.True) isMuted = true;
                else if (isMutedEl.ValueKind == JsonValueKind.False) isMuted = false;
                else if (isMutedEl.ValueKind == JsonValueKind.Number && isMutedEl.TryGetInt32(out int mutedNum)) isMuted = mutedNum != 0;
            }
            if (chat.TryGetProperty("mute_until", out var muteUntilEl) &&
                muteUntilEl.ValueKind == JsonValueKind.Number &&
                muteUntilEl.TryGetInt64(out long muteUntilNum))
                muteUntil = muteUntilNum;
        }
        string name = "", provider = "", picture = "";
        if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id_profile", out var idProfile))
            CopyProfileFields(idProfile, ref name, ref provider, ref picture);
        if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id_profiles", out var idProfiles) &&
            idProfiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in idProfiles.EnumerateArray())
            {
                if (name.Length > 0 && provider.Length > 0 && picture.Length > 0) break;
                CopyProfileFields(p, ref name, ref provider, ref picture);
            }
        }
        return new AccountSession(token, userId, email, isMuted, muteUntil, name, provider, picture);
    }

    private static void CopyProfileFields(JsonElement idProfile, ref string name, ref string provider, ref string picture)
    {
        if (idProfile.ValueKind != JsonValueKind.Object) return;
        if (name.Length == 0 && idProfile.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            name = n.GetString() ?? "";
        if (provider.Length == 0 && idProfile.TryGetProperty("provider", out var pr) && pr.ValueKind == JsonValueKind.String)
            provider = pr.GetString() ?? "";
        if (picture.Length == 0 && idProfile.TryGetProperty("picture", out var pic) && pic.ValueKind == JsonValueKind.String)
            picture = pic.GetString() ?? "";
    }

    private static void AddLoginHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", "DeepSeek/2.5.0 Android/35");
        req.Headers.TryAddWithoutValidation("X-Client-Version", "2.5.0");
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "android");
        req.Headers.TryAddWithoutValidation("X-Client-Locale", "en_US");
        req.Headers.TryAddWithoutValidation("X-Client-Bundle-Id", "com.deepseek.chat");
        req.Headers.TryAddWithoutValidation("X-Device-Id", SettingsStore.GetOrCreateDeviceId());
        req.Headers.TryAddWithoutValidation("X-Client-Timezone-Offset", "0");
        req.Headers.TryAddWithoutValidation("Referer", "https://chat.deepseek.com/sign_in");
    }

    private static string FriendlyMessage(string bizMsg) => bizMsg switch
    {
        "PASSWORD_OR_USER_NAME_IS_WRONG" => "Wrong email or password",
        "USER_IS_BANNED" => "This account is banned",
        "RISK_DEVICE_DETECTED" => "Device flagged as risky — try again later",
        _ => string.IsNullOrWhiteSpace(bizMsg) ? "Sign-in failed." : $"Sign-in failed: {bizMsg}"
    };
}