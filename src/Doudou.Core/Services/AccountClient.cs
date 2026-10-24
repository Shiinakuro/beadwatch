using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Doudou.Core.Services;

/// <summary>云端账号 API。只同步偏好与历史摘要，绝不发送 API Key。</summary>
public sealed class AccountClient
{
    public const string SiteBase = "https://beadwatch-cloud-7c28q17g8xc.qoder.zone";
    public const string CloudBase = SiteBase + "/functions/v1/app";
    public const string AccountPage = SiteBase + "/account.html";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class CloudUser
    {
        public string Id { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public sealed class AuthResult
    {
        public CloudUser User { get; set; } = new();
        public string Token { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class CloudSettingsPayload
    {
        [JsonPropertyName("DefaultBrand")] public string? DefaultBrand { get; set; }
        [JsonPropertyName("Author")] public string? Author { get; set; }
        [JsonPropertyName("Watermark")] public string? Watermark { get; set; }
        [JsonPropertyName("BeadPitchMm")] public double? BeadPitchMm { get; set; }
        [JsonPropertyName("ImageModel")] public string? ImageModel { get; set; }

        public void ApplyTo(AppSettings s, bool save)
        {
            if (DefaultBrand != null) s.DefaultBrand = DefaultBrand;
            if (Author != null) s.Author = Author;
            if (Watermark != null) s.Watermark = Watermark;
            if (BeadPitchMm is { } pitch && pitch >= 1 && pitch <= 10) s.BeadPitchMm = pitch;
            if (ImageModel != null) s.ImageModel = ImageModel;
            if (save) s.Save();
        }
    }

    public sealed class SyncedSettings
    {
        [JsonPropertyName("DefaultBrand")] public string DefaultBrand { get; set; } = "";
        [JsonPropertyName("Author")] public string Author { get; set; } = "";
        [JsonPropertyName("Watermark")] public string Watermark { get; set; } = "";
        [JsonPropertyName("BeadPitchMm")] public double BeadPitchMm { get; set; } = 2.6;
        [JsonPropertyName("ImageModel")] public string ImageModel { get; set; } = "";

        public static SyncedSettings From(AppSettings s) => new()
        {
            DefaultBrand = s.DefaultBrand,
            Author = s.Author,
            Watermark = s.Watermark,
            BeadPitchMm = s.BeadPitchMm,
            ImageModel = s.ImageModel,
        };
    }

    public sealed class CloudSettingsResponse
    {
        public CloudSettingsPayload? Settings { get; set; }
        public int Version { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class UserResponse { public CloudUser User { get; set; } = new(); }
    private sealed class HistoryResponse { public List<HistoryEntry> Entries { get; set; } = new(); }
    private sealed class PushHistoryResponse { public int Accepted { get; set; } }

    public sealed class AccountApiException : Exception
    {
        public string Code { get; }
        public int Status { get; }
        public string? Detail { get; }
        public AccountApiException(string code, int status, string? detail = null) : base(code)
        {
            Code = code;
            Status = status;
            Detail = detail;
        }
    }

    public static string Friendly(string code) => code switch
    {
        "invalid_credentials" => "邮箱或密码不对。",
        "invalid_email" => "邮箱格式不对。",
        "invalid_password" => "密码至少 10 位。",
        "invalid_display_name" => "称呼太长。",
        "email_taken" => "这个邮箱已经注册过了，直接登录即可。",
        "rate_limited" => "尝试次数太多，请稍后再试。",
        "login_required" => "登录已过期，请重新登录。",
        "network" => "连接不上云端，请检查网络后重试。",
        "database_unavailable" or "service_unavailable" => "云端服务暂时不可用，请稍后重试。",
        "write_conflict" => "设置刚刚被另一台设备更新，请刷新后再试。",
        "logout_unconfirmed" => "退出请求未确认，请稍后重试。",
        "sites_gateway_origin_forbidden" => "云端网关拒绝了本机请求，请更新到最新版本后重试。",
        "BOT_DETECTED" => "云端网关拦截了本机请求，请更新到最新版本后重试。",
        _ => "操作失败，请稍后重试。",
    };

    public Task<AuthResult> RegisterAsync(string email, string password, string displayName, CancellationToken ct = default) =>
        SendAsync<AuthResult>(HttpMethod.Post, "/api/desktop/register", null,
            new { email, password, displayName }, ct);

    public Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default) =>
        SendAsync<AuthResult>(HttpMethod.Post, "/api/desktop/login", null, new { email, password }, ct);

    public Task LogoutAsync(string token, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/desktop/logout", token, new { }, ct);

    public async Task<CloudUser> MeAsync(string token, CancellationToken ct = default) =>
        (await SendAsync<UserResponse>(HttpMethod.Get, "/api/me", token, null, ct)).User;

    public Task<CloudSettingsResponse> GetSettingsAsync(string token, CancellationToken ct = default) =>
        SendAsync<CloudSettingsResponse>(HttpMethod.Get, "/api/settings", token, null, ct);

    public Task<CloudSettingsResponse> PutSettingsAsync(string token, SyncedSettings settings, CancellationToken ct = default) =>
        SendAsync<CloudSettingsResponse>(HttpMethod.Put, "/api/settings", token, new { settings }, ct);

    public async Task<List<HistoryEntry>> GetHistoryAsync(string token, CancellationToken ct = default) =>
        (await SendAsync<HistoryResponse>(HttpMethod.Get, "/api/history", token, null, ct)).Entries;

    public async Task PushHistoryAsync(string token, IReadOnlyList<HistoryEntry> entries, CancellationToken ct = default)
    {
        foreach (var batch in entries.Chunk(40))
        {
            if (batch.Length == 0) continue;
            await SendAsync<PushHistoryResponse>(HttpMethod.Post, "/api/history", token,
                new { entries = batch }, ct);
        }
    }

    private static async Task<T> SendAsync<T>(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, CloudBase + path);
        request.Headers.Accept.ParseAdd("application/json");
        // Qoder's gateway requires the site origin on non-browser requests too.
        request.Headers.TryAddWithoutValidation("Origin", SiteBase);
        request.Headers.TryAddWithoutValidation("User-Agent", "BEADWATCH-Windows/1.3 (+https://beadwatch-cloud-7c28q17g8xc.qoder.zone)");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new AccountApiException("network", 0); }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = "request_failed";
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        code = error.GetString() ?? code;
                    else if (doc.RootElement.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String)
                        code = codeValue.GetString() ?? code;
                }
                catch (JsonException) { }
                if (response.StatusCode == HttpStatusCode.Unauthorized) code = "login_required";
                throw new AccountApiException(code, (int)response.StatusCode, text.Length > 500 ? text[..500] : text);
            }
            try
            {
                return JsonSerializer.Deserialize<T>(text, Json)
                    ?? throw new AccountApiException("bad_response", (int)response.StatusCode);
            }
            catch (JsonException) { throw new AccountApiException("bad_response", (int)response.StatusCode); }
        }
    }
}
