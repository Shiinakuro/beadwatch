using System.Security.Cryptography;
using System.Text.Json;

namespace Doudou.Core.Services;

/// <summary>云端只保存这个信封；桌面端用账号密码派生的密钥解开。</summary>
public sealed class EncryptedSecrets
{
    public int V { get; set; } = 1;
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; } = KeySyncCrypto.Iterations;
    public string Salt { get; set; } = "";
    public string Iv { get; set; } = "";
    public string Data { get; set; } = "";
    public string Tag { get; set; } = "";
}

public sealed class SecretBundle
{
    public string ImageApiKey { get; set; } = "";
    public string Hunyuan3dApiKey { get; set; } = "";
    public string ImageApiBase { get; set; } = "";
    public string Hunyuan3dApiBase { get; set; } = "";

    public static SecretBundle From(AppSettings s) => new()
    {
        ImageApiKey = s.ImageApiKey,
        Hunyuan3dApiKey = s.Hunyuan3dApiKey,
        ImageApiBase = s.ImageApiBase,
        Hunyuan3dApiBase = s.Hunyuan3dApiBase,
    };

    public void ApplyTo(AppSettings s)
    {
        s.ImageApiKey = ImageApiKey ?? "";
        s.Hunyuan3dApiKey = Hunyuan3dApiKey ?? "";
        s.ImageApiBase = ImageApiBase ?? "";
        s.Hunyuan3dApiBase = Hunyuan3dApiBase ?? "";
    }
}

/// <summary>AES-GCM + PBKDF2。服务端只存密文，没有账号密码无法还原 API Key。</summary>
public static class KeySyncCrypto
{
    public const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int IvBytes = 12;
    private const int TagBytes = 16;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] DeriveKey(string password, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Math.Clamp(iterations, 100_000, 1_000_000), HashAlgorithmName.SHA256, 32);

    public static EncryptedSecrets Encrypt(SecretBundle bundle, byte[] key, byte[] salt)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(bundle, Json);
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var data = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(iv, plain, data, tag);
        return new EncryptedSecrets
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Iv = Convert.ToBase64String(iv),
            Data = Convert.ToBase64String(data),
            Tag = Convert.ToBase64String(tag),
        };
    }

    public static SecretBundle Decrypt(EncryptedSecrets envelope, byte[] key)
    {
        if (envelope == null || envelope.V != 1 || envelope.Kdf != "PBKDF2-SHA256") throw new CryptographicException("unsupported_envelope");
        var iv = Convert.FromBase64String(envelope.Iv);
        var data = Convert.FromBase64String(envelope.Data);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (iv.Length != IvBytes || tag.Length != TagBytes) throw new CryptographicException("invalid_envelope");
        var plain = new byte[data.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(iv, data, tag, plain);
        return JsonSerializer.Deserialize<SecretBundle>(plain, Json) ?? new SecretBundle();
    }

    public static byte[] KeyFromPassword(string password, EncryptedSecrets envelope) =>
        DeriveKey(password, Convert.FromBase64String(envelope.Salt), envelope.Iterations);
}
