using Doudou.Core.Services;
using Doudou.Editor;

namespace Doudou.WinUI;

/// <summary>桌面端账号状态：令牌、云端设置摘要与历史同步。API Key 永不参与同步。</summary>
public sealed class AccountManager
{
    private readonly Session _s;
    private readonly AccountClient _client = new();
    private readonly object _gate = new();
    private StoredAccount? _session;
    private List<HistoryEntry> _history = HistoryStore.Load();
    private bool _applyingSettings;

    public AccountManager(Session session)
    {
        _s = session;
        _session = AccountVault.Load();
        session.Settings.Saved += OnSettingsSaved;
    }

    public AccountClient Client => _client;
    public StoredAccount? Session { get { lock (_gate) return _session; } }
    public bool IsLoggedIn => Session != null;
    public AccountClient.CloudSettingsResponse? CloudSettings { get; private set; }
    public string? LastSyncError { get; private set; }
    public IReadOnlyList<HistoryEntry> History { get { lock (_gate) return _history.ToList(); } }

    public event Action? Changed;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (session == null) return;
        try
        {
            var user = await _client.MeAsync(session.Token, ct);
            lock (_gate)
                _session = session with { Email = user.Email, DisplayName = user.DisplayName };
            AccountVault.Save(_session!);
            CloudSettings = await _client.GetSettingsAsync(session.Token, ct);
            var cloud = await _client.GetHistoryAsync(session.Token, ct);
            lock (_gate) _history = HistoryStore.Merge(cloud);
            LastSyncError = null;
            Changed?.Invoke();
        }
        catch (AccountClient.AccountApiException e) when (e.Status == 401)
        {
            ClearSession();
        }
        catch (Exception)
        {
            // 离线时保留本地会话，下次打开账号窗口或联网后再同步。
            LastSyncError = "暂时连接不上云端；本地设置和历史仍可用。";
            Changed?.Invoke();
        }
    }

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var result = await _client.LoginAsync(email.Trim(), password, ct);
        StoreSession(result);
        await AfterAuthAsync(ct);
    }

    public async Task RegisterAsync(string email, string password, string displayName, CancellationToken ct = default)
    {
        var result = await _client.RegisterAsync(email.Trim(), password, displayName.Trim(), ct);
        StoreSession(result);
        await AfterAuthAsync(ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (session != null)
        {
            try { await _client.LogoutAsync(session.Token, ct); }
            catch (Exception) { /* 退出以本地清除为准，云端会话到期后自然失效 */ }
        }
        ClearSession();
    }

    public async Task<AccountClient.CloudSettingsResponse> PushSettingsAsync(CancellationToken ct = default)
    {
        var session = RequireSession();
        var result = await _client.PutSettingsAsync(session.Token, AccountClient.SyncedSettings.From(_s.Settings), ct);
        CloudSettings = result;
        LastSyncError = null;
        Changed?.Invoke();
        return result;
    }

    public async Task<AccountClient.CloudSettingsResponse> RefreshSettingsAsync(CancellationToken ct = default)
    {
        var session = RequireSession();
        CloudSettings = await _client.GetSettingsAsync(session.Token, ct);
        LastSyncError = null;
        Changed?.Invoke();
        return CloudSettings;
    }

    public async Task<AccountClient.CloudSettingsResponse> ApplyCloudSettingsAsync(CancellationToken ct = default)
    {
        var result = await RefreshSettingsAsync(ct);
        if (result.Settings != null)
        {
            _applyingSettings = true;
            try { result.Settings.ApplyTo(_s.Settings, save: true); }
            finally { _applyingSettings = false; }
        }
        Changed?.Invoke();
        return result;
    }

    public async Task SyncHistoryAsync(CancellationToken ct = default)
    {
        var session = RequireSession();
        var cloud = await _client.GetHistoryAsync(session.Token, ct);
        var cloudIds = cloud.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = HistoryStore.Load();
        lock (_gate) _history = HistoryStore.Merge(cloud);
        var toPush = local.Where(e => !cloudIds.Contains(e.Id)).Take(120).ToList();
        if (toPush.Count > 0) await _client.PushHistoryAsync(session.Token, toPush, ct);
        LastSyncError = null;
        Changed?.Invoke();
    }

    public void RecordHistory(HistoryEntry entry)
    {
        HistoryStore.Add(entry);
        lock (_gate) _history = HistoryStore.Load();
        if (Session != null) _ = PushHistoryQuietAsync(entry);
        Changed?.Invoke();
    }

    private void StoreSession(AccountClient.AuthResult result)
    {
        var stored = new StoredAccount(result.Token, result.User.Email, result.User.DisplayName, result.ExpiresAt);
        lock (_gate) _session = stored;
        AccountVault.Save(stored);
        Changed?.Invoke();
    }

    private async Task AfterAuthAsync(CancellationToken ct)
    {
        try
        {
            CloudSettings = await _client.GetSettingsAsync(Session!.Token, ct);
            if (CloudSettings.Settings == null)
                CloudSettings = await PushSettingsAsync(ct);
            await SyncHistoryAsync(ct);
            LastSyncError = null;
        }
        catch (Exception e)
        {
            LastSyncError = e is AccountClient.AccountApiException api
                ? AccountClient.Friendly(api.Code)
                : "登录成功，但暂时无法同步云端数据。";
        }
        Changed?.Invoke();
    }

    private async Task PushHistoryQuietAsync(HistoryEntry entry)
    {
        try
        {
            var session = Session;
            if (session != null) await _client.PushHistoryAsync(session.Token, new[] { entry });
        }
        catch (Exception) { /* 下次手动同步会补传 */ }
    }

    private async Task PushSettingsQuietAsync()
    {
        if (_applyingSettings || Session == null) return;
        try
        {
            await PushSettingsAsync();
        }
        catch (Exception e)
        {
            LastSyncError = e is AccountClient.AccountApiException api
                ? AccountClient.Friendly(api.Code)
                : "设置已保存在本机，但暂时没有同步到云端。";
            Changed?.Invoke();
        }
    }

    private void OnSettingsSaved()
    {
        if (_applyingSettings || Session == null) return;
        _ = PushSettingsQuietAsync();
    }

    private StoredAccount RequireSession() =>
        Session ?? throw new AccountClient.AccountApiException("login_required", 401);

    private void ClearSession()
    {
        lock (_gate) _session = null;
        CloudSettings = null;
        AccountVault.Clear();
        LastSyncError = null;
        Changed?.Invoke();
    }
}
