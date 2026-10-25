using Doudou.Core.Services;
using Doudou.Editor;

namespace Doudou.WinUI;

/// <summary>桌面端账号状态：令牌、云端设置、加密 API Key 与历史同步。</summary>
public sealed class AccountManager
{
    private readonly Session _s;
    private readonly AccountClient _client = new();
    private readonly object _gate = new();
    private StoredAccount? _session;
    private List<HistoryEntry> _history = HistoryStore.Load();
    private byte[]? _keyKey;
    private byte[]? _keySalt;
    private bool _keysLocked;
    private bool _applyingSettings;

    public AccountManager(Session session)
    {
        _s = session;
        _session = AccountVault.Load();
        if (!string.IsNullOrWhiteSpace(_session?.KeyKey) && !string.IsNullOrWhiteSpace(_session.KeySalt))
        {
            try
            {
                _keyKey = Convert.FromBase64String(_session.KeyKey);
                _keySalt = Convert.FromBase64String(_session.KeySalt);
            }
            catch (FormatException) { _keyKey = null; _keySalt = null; }
        }
        session.Settings.Saved += OnSettingsSaved;
    }

    public AccountClient Client => _client;
    public StoredAccount? Session { get { lock (_gate) return _session; } }
    public bool IsLoggedIn => Session != null;
    public bool HasKeyAccess => _keyKey != null;
    public bool KeysLocked => _keysLocked;
    public AccountClient.CloudSettingsResponse? CloudSettings { get; private set; }
    public string? LastSyncError { get; private set; }
    public IReadOnlyList<HistoryEntry> History { get { lock (_gate) return _history.ToList(); } }

    public event Action? Changed;
    public event Action? SettingsApplied;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (session == null) return;
        try
        {
            var user = await _client.MeAsync(session.Token, ct);
            lock (_gate)
                _session = session with { Email = user.Email, DisplayName = user.DisplayName };
            CloudSettings = await _client.GetSettingsAsync(session.Token, ct);
            if (CloudSettings.Settings?.Secrets != null)
            {
                if (_keyKey != null)
                {
                    try
                    {
                        KeySyncCrypto.Decrypt(CloudSettings.Settings.Secrets, _keyKey);
                        _keysLocked = false;
                    }
                    catch (Exception) { _keysLocked = true; }
                }
                else _keysLocked = true;
            }
            await TryApplyMissingSecretsAsync(ct);
            var cloud = await _client.GetHistoryAsync(session.Token, ct);
            lock (_gate) _history = HistoryStore.Merge(cloud);
            SaveVault();
            LastSyncError = null;
            Changed?.Invoke();
        }
        catch (AccountClient.AccountApiException e) when (e.Status == 401)
        {
            ClearSession();
        }
        catch (Exception)
        {
            LastSyncError = "暂时连接不上云端，稍后会自动重试。";
            Changed?.Invoke();
        }
    }

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var result = await _client.LoginAsync(email.Trim(), password, ct);
        StoreSession(result);
        await AfterAuthAsync(password, ct);
    }

    public async Task RegisterAsync(string email, string password, string displayName, CancellationToken ct = default)
    {
        var result = await _client.RegisterAsync(email.Trim(), password, displayName.Trim(), ct);
        StoreSession(result);
        await AfterAuthAsync(password, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (session != null)
        {
            try { await _client.LogoutAsync(session.Token, ct); }
            catch (Exception) { /* 本地清除即可，云端会话到期后失效 */ }
        }
        ClearSession();
    }

    public async Task<AccountClient.CloudSettingsResponse> PushSettingsAsync(CancellationToken ct = default)
    {
        var session = RequireSession();
        var synced = AccountClient.SyncedSettings.From(_s.Settings);
        var hasKeys = !string.IsNullOrWhiteSpace(_s.Settings.ImageApiKey) || !string.IsNullOrWhiteSpace(_s.Settings.Hunyuan3dApiKey);
        if (hasKeys)
        {
            if (_keyKey == null) LastSyncError = "API Key 已加密，重新登录后可同步。";
            else
            {
                var salt = ExistingSalt() ?? _keySalt;
                if (salt == null)
                {
                    salt = KeySyncCrypto.NewSalt();
                    _keySalt = salt;
                }
                synced.Secrets = KeySyncCrypto.Encrypt(SecretBundle.From(_s.Settings), _keyKey, salt);
            }
        }
        var result = await _client.PutSettingsAsync(session.Token, synced, ct);
        CloudSettings = result;
        SaveVault();
        if (hasKeys && _keyKey != null) LastSyncError = null;
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
            try
            {
                result.Settings.ApplyTo(_s.Settings, save: false);
                if (result.Settings.Secrets != null)
                {
                    if (_keyKey != null)
                    {
                        try
                        {
                            KeySyncCrypto.Decrypt(result.Settings.Secrets, _keyKey).ApplyTo(_s.Settings);
                            _keysLocked = false;
                        }
                        catch (Exception) { _keysLocked = true; }
                    }
                    else _keysLocked = true;
                }
                _s.Settings.Save();
            }
            finally { _applyingSettings = false; }
            SettingsApplied?.Invoke();
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
        _keyKey = null;
        _keySalt = null;
        _keysLocked = false;
        AccountVault.Save(stored);
        Changed?.Invoke();
    }

    private async Task AfterAuthAsync(string password, CancellationToken ct)
    {
        try
        {
            CloudSettings = await _client.GetSettingsAsync(Session!.Token, ct);
            await UnlockKeysAsync(password);
            if (CloudSettings.Settings == null) CloudSettings = await PushSettingsAsync(ct);
            else await TryApplyMissingSecretsAsync(ct);
            await SyncHistoryAsync(ct);
            LastSyncError = null;
        }
        catch (Exception e)
        {
            LastSyncError = e is AccountClient.AccountApiException api
                ? AccountClient.Friendly(api.Code)
                : "登录成功，稍后会自动同步云端数据。";
        }
        SaveVault();
        Changed?.Invoke();
    }

    private async Task UnlockKeysAsync(string password)
    {
        var envelope = CloudSettings?.Settings?.Secrets;
        if (envelope != null)
        {
            try
            {
                var key = KeySyncCrypto.KeyFromPassword(password, envelope);
                KeySyncCrypto.Decrypt(envelope, key);
                _keyKey = key;
                _keySalt = Convert.FromBase64String(envelope.Salt);
                _keysLocked = false;
            }
            catch (Exception)
            {
                _keyKey = null;
                _keySalt = null;
                _keysLocked = true;
            }
        }
        else
        {
            _keySalt = KeySyncCrypto.NewSalt();
            _keyKey = KeySyncCrypto.DeriveKey(password, _keySalt);
            _keysLocked = false;
        }
        SaveVault();
        await Task.CompletedTask;
    }

    private async Task TryApplyMissingSecretsAsync(CancellationToken ct)
    {
        var envelope = CloudSettings?.Settings?.Secrets;
        if (envelope == null || _keyKey == null) return;
        if (_s.Settings.ImageApiKey.Length > 0 || _s.Settings.Hunyuan3dApiKey.Length > 0) return;
        try
        {
            var bundle = KeySyncCrypto.Decrypt(envelope, _keyKey);
            _applyingSettings = true;
            try
            {
                bundle.ApplyTo(_s.Settings);
                _s.Settings.Save();
            }
            finally { _applyingSettings = false; }
            _keysLocked = false;
            SettingsApplied?.Invoke();
        }
        catch (Exception) { _keysLocked = true; }
        await Task.CompletedTask;
    }

    private async Task PushHistoryQuietAsync(HistoryEntry entry)
    {
        try
        {
            var session = Session;
            if (session != null) await _client.PushHistoryAsync(session.Token, new[] { entry });
        }
        catch (Exception) { /* 下次同步补传 */ }
    }

    private async Task PushSettingsQuietAsync()
    {
        if (_applyingSettings || Session == null) return;
        try { await PushSettingsAsync(); }
        catch (Exception e)
        {
            LastSyncError = e is AccountClient.AccountApiException api
                ? AccountClient.Friendly(api.Code)
                : "设置已保存在本机，稍后会重试云端同步。";
            Changed?.Invoke();
        }
    }

    private void OnSettingsSaved()
    {
        if (_applyingSettings || Session == null) return;
        _ = PushSettingsQuietAsync();
    }

    private byte[]? ExistingSalt()
    {
        var salt = CloudSettings?.Settings?.Secrets?.Salt;
        if (string.IsNullOrWhiteSpace(salt)) return null;
        try { return Convert.FromBase64String(salt); }
        catch (FormatException) { return null; }
    }

    private void SaveVault()
    {
        var session = Session;
        if (session == null) return;
        var updated = session with
        {
            KeyKey = _keyKey != null ? Convert.ToBase64String(_keyKey) : null,
            KeySalt = _keySalt != null ? Convert.ToBase64String(_keySalt) : null,
        };
        lock (_gate) _session = updated;
        AccountVault.Save(updated);
    }

    private StoredAccount RequireSession() =>
        Session ?? throw new AccountClient.AccountApiException("login_required", 401);

    private void ClearSession()
    {
        lock (_gate) _session = null;
        _keyKey = null;
        _keySalt = null;
        _keysLocked = false;
        CloudSettings = null;
        AccountVault.Clear();
        LastSyncError = null;
        Changed?.Invoke();
    }
}
