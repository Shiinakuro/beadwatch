using System.Security.Cryptography;
using Doudou.Core.Imaging;
using Doudou.Core.Services;

namespace Doudou.Core.Recon;

/// <summary>
/// 照片预处理：按用户框选的主体裁剪，再去背景（有本地抠图模型时用 ISNet，否则按背景色泛洪）。
/// 结果按「原图内容 + 框」缓存：同一张图同一个框永远得到同一个文件——这样后面的 AI 生图缓存也能稳定命中。
/// </summary>
public static class SubjectPrep
{
    public static string CacheDir => Path.Combine(AppPaths.UserData, "prep_cache");

    public static string Key(string src, (int X0, int Y0, int X1, int Y1)? box, bool cutout)
    {
        var h = SHA256.HashData(File.ReadAllBytes(src));
        var tag = box is { } b ? $"{b.X0},{b.Y0},{b.X1},{b.Y1}" : "full";
        var all = SHA256.HashData(h.Concat(System.Text.Encoding.UTF8.GetBytes(tag + (cutout ? "|cut" : "|crop"))).ToArray());
        return Convert.ToHexString(all)[..24].ToLowerInvariant();
    }

    /// <summary>返回预处理后的 PNG 路径（透明底）。</summary>
    public static async Task<string> PrepareAsync(string src, (int X0, int Y0, int X1, int Y1)? box, Hy3dClient? local,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        bool useCut = local != null && Hy3dClient.CutoutInstalled;
        Directory.CreateDirectory(CacheDir);
        var outp = Path.Combine(CacheDir, Key(src, box, useCut) + ".png");
        if (File.Exists(outp) && new FileInfo(outp).Length > 0) return outp;
        if (useCut)
        {
            try
            {
                await local!.CutoutAsync(src, outp, box, progress, ct);
                return outp;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                progress?.Report("本地抠图失败，改用背景色识别：" + e.Message);
            }
        }
        // 兜底：只裁剪，背景交给 Silhouette 的背景色识别；输出透明底
        var img = RgbaImage.Load(src);
        if (box is { } b) img = img.Crop(b.X0, b.Y0, Math.Max(1, b.X1 - b.X0), Math.Max(1, b.Y1 - b.Y0));
        var sil = Silhouette.FromImage(img);
        sil.ApplyTo(img).SavePng(outp);
        return outp;
    }
}
