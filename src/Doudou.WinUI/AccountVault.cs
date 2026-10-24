using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Doudou.Core.Services;

namespace Doudou.WinUI;

public sealed record StoredAccount(string Token, string Email, string DisplayName, DateTimeOffset ExpiresAt);

/// <summary>用 Windows DPAPI（当前用户）保存云端令牌；明文令牌不落盘。</summary>
public static class AccountVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BEADWATCH.Account.v1");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Path => System.IO.Path.Combine(AppPaths.UserData, "account.dat");

    public static StoredAccount? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            var account = JsonSerializer.Deserialize<StoredAccount>(plain, Json);
            if (account == null || string.IsNullOrWhiteSpace(account.Token)) return null;
            if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) { Clear(); return null; }
            return account;
        }
        catch (Exception) { return null; }
    }

    public static void Save(StoredAccount account)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserData);
            var plain = JsonSerializer.SerializeToUtf8Bytes(account, Json);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tmp = Path + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception) { /* 凭据保存失败不应影响本次登录 */ }
    }

    public static void Clear()
    {
        try { File.Delete(Path); } catch (Exception) { }
    }
}
