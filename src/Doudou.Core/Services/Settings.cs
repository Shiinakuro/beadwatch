using System.Text.Json;

namespace Doudou.Core.Services;

/// <summary>用户设置（保存在 userdata/settings.json，不进源码）。</summary>
public sealed class AppSettings
{
    public string ImageApiBase { get; set; } = "";
    public string ImageApiKey { get; set; } = "";
    public string ImageModel { get; set; } = "";
    /// <summary>混元 3D 在线 API（AI3D 的 OpenAI 兼容接口）。</summary>
    public string Hunyuan3dApiBase { get; set; } = "https://api.ai3d.cloud.tencent.com";
    public string Hunyuan3dApiKey { get; set; } = "";
    public string DefaultBrand { get; set; } = "mard221";
    public string Author { get; set; } = "";
    public string Watermark { get; set; } = "";
    public double BeadPitchMm { get; set; } = 2.6;
    /// <summary>看过新手引导后置为 true，之后不再自动弹出。</summary>
    public bool OnboardingDone { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            if (File.Exists(AppPaths.Settings))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new AppSettings();
            else
                return new AppSettings();
        }
        catch (Exception) { return new AppSettings(); }

        // 旧版预填过第三方地址和模型名。没填过 Key 的视为从未配置，清空，避免把预填值当成自己的服务。
        if (s.ImageApiKey.Length == 0 && s.ImageApiBase.Trim().TrimEnd('/').Equals("https://api.centos.hk", StringComparison.OrdinalIgnoreCase))
        {
            s.ImageApiBase = "";
            s.ImageModel = "";
        }
        return s;
    }

    public event Action? Saved;

    public void Save()
    {
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
        Saved?.Invoke();
    }
}
