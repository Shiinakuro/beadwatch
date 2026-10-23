using System.Net.Http.Headers;
using System.Text.Json;
using Doudou.Core.Imaging;
using Doudou.Core.Recon;

namespace Doudou.Core.Services;

/// <summary>
/// 图像大模型（OpenAI 兼容 /v1/images/edits）。用途：
/// 1) 由已有照片补全缺失的标准视图（正/背/左/右）；
/// 2) 把杂乱背景的照片整理成白底正交视图；
/// 3) 生成「平涂像素风」版本，只用来取色，让量化更干净。
/// 不让它直接画豆图——格子对不齐。
/// </summary>
public sealed class ImageAi
{
    private readonly AppSettings _s;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    public ImageAi(AppSettings s) => _s = s;

    public bool HasEndpoint => !string.IsNullOrWhiteSpace(_s.ImageApiKey) && !string.IsNullOrWhiteSpace(_s.ImageApiBase);

    public bool Configured => HasEndpoint && !string.IsNullOrWhiteSpace(_s.ImageModel);

    /// <summary>
    /// 服务根地址。用户常把完整接口 /v1/images/edits 或 /v1 粘进来；统一去掉，避免拼出 /v1/v1/...。
    /// 没写协议时补 https://。
    /// </summary>
    public static string NormalizeBase(string? raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        foreach (var suffix in new[] { "/v1/images/edits", "/images/edits", "/v1/images/generations", "/v1" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length].TrimEnd('/');
                break;
            }
        return s;
    }

    public static string ModelsUrl(string? baseUrl) => NormalizeBase(baseUrl) + "/v1/models";
    public static string EditsUrl(string? baseUrl) => NormalizeBase(baseUrl) + "/v1/images/edits";

    public static string ViewPrompt(ViewKind target, string? subjectHint) =>
        (string.IsNullOrWhiteSpace(subjectHint) ? "" : $"Subject: {subjectHint}. ") + target switch
        {
            ViewKind.Front => "Show this exact same character/object from the FRONT, facing the camera directly.",
            ViewKind.Back => "Show this exact same character/object from directly BEHIND (back view), as if the camera walked around to its back.",
            ViewKind.Left => "Show this exact same character/object from its LEFT side (pure side profile), so it faces the LEFT edge of the image.",
            ViewKind.Right => "Show this exact same character/object from its RIGHT side (pure side profile), so it faces the RIGHT edge of the image.",
            ViewKind.Top => "Show this exact same character/object from directly ABOVE (top-down view), its front pointing to the bottom of the image.",
            _ => "Show this exact same character/object from directly BELOW.",
        } + " Keep identical proportions, colors, clothing and details. Orthographic camera, no perspective, full body fully visible and centered," +
            " same scale as the reference, a perfectly flat solid pure magenta (#FF00FF) background filling the entire canvas (chroma-key screen, no gradient, no checkerboard), no shadow, no ground, no text.";

    /// <summary>
    /// 卡通化正视图：把实物重新设计成适合拼豆/体素的卡通造型——形体更大块、特征更夸张、纯平涂色，
    /// 细碎结构（花瓣、挂环、五官）放大到在 40~80 颗分辨率下仍可辨认。
    /// </summary>
    public static string CartoonFrontPrompt(string? subjectHint) =>
        "Redesign the object in the reference photos as a cute chunky CARTOON figure that will be rebuilt with fuse beads (voxel art) " +
        "at only about 50 beads wide, so it must read clearly as BIG FLAT COLOR BLOCKS. " +
        (string.IsNullOrWhiteSpace(subjectHint) ? "" : "Subject: " + subjectHint + ". ") +
        "Rules: NO black or dark outlines anywhere — separate neighbouring shapes only by clearly different solid colors " +
        "(e.g. two tones of orange for alternating petals); enlarge small details (flowers, petals, loops) so each part is big and simple; " +
        "facial features (eyes, mouth) are the ONLY dark marks and must be bold thick strokes; skin/face color must be clearly " +
        "different from the body color; at most 10 distinct flat colors, no gradients, no highlights, no gloss, no texture, no tiny patterns. " +
        "Keep the recognizable silhouette and color scheme. Exact FRONT view, orthographic camera, centered, full object visible, " +
        "a perfectly flat solid pure magenta (#FF00FF) background filling the entire canvas (chroma-key screen, no gradient, no checkerboard), no shadow, no text.";

    /// <summary>以卡通正视图为准，生成其他视角（同一设计）。</summary>
    public static string CartoonViewPrompt(ViewKind target) =>
        "The FIRST image is the authoritative cartoon design (front view). Draw the EXACT SAME cartoon design, same flat color blocks " +
        "(no outlines), same " +
        "proportions and same scale, " + target switch
        {
            ViewKind.Back => "seen from directly BEHIND (back view).",
            ViewKind.Left => "seen from its LEFT side as a pure side profile, facing the LEFT edge of the image.",
            ViewKind.Right => "seen from its RIGHT side as a pure side profile, facing the RIGHT edge of the image.",
            ViewKind.Top => "seen from directly ABOVE, front pointing down in the image.",
            _ => "seen from the front.",
        } + " Orthographic camera, centered, full object visible, a perfectly flat solid pure magenta (#FF00FF) background filling the entire canvas (chroma-key screen, no gradient, no checkerboard), flat cel shading, no shadow, no text.";

    public const string CleanupPrompt =
        "Recreate this exact object as a clean product reference image: same pose, same viewing angle, same proportions and colors. " +
        "Orthographic camera, full object visible and centered, a perfectly flat solid pure magenta (#FF00FF) background filling the entire canvas (chroma-key screen, no gradient, no checkerboard), no shadow, no text.";

    public const string FlatColorPrompt =
        "Repaint this exact image as flat-shaded cel art for bead crafts: keep the identical silhouette, pose, framing and scale; " +
        "use large flat areas of solid color, remove gradients, highlights, reflections and soft shading; keep small important details " +
        "such as eyes, mouth and outlines crisp with solid dark colors. Use a perfectly flat solid pure magenta (#FF00FF) background filling the entire canvas (chroma-key screen, no gradient, no checkerboard), no text.";

    /// <summary>为 true 时忽略缓存、总是重新生成。</summary>
    public bool ForceRegenerate { get; set; }

    /// <summary>最近一次 EditAsync 是否命中了缓存。</summary>
    public bool LastFromCache { get; private set; }

    public static string CacheDir => Path.Combine(AppPaths.UserData, "ai_cache");

    /// <summary>
    /// 缓存键 = 模型 + 提示词 + 是否透明 + 每张参考图的内容哈希。
    /// 同一组参考图、同样的设置再生成时直接复用，不消耗额度。
    /// </summary>
    public string CacheKey(IReadOnlyList<string> referenceImages, string prompt, bool transparent)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        void Add(byte[] b) => sha.TransformBlock(b, 0, b.Length, null, 0);
        Add(System.Text.Encoding.UTF8.GetBytes(string.Join('|', _s.ImageModel, prompt, transparent)));
        foreach (var p in referenceImages) Add(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!)[..32].ToLowerInvariant();
    }

    public string? CachedPath(IReadOnlyList<string> referenceImages, string prompt, bool transparent = false)
    {
        var p = Path.Combine(CacheDir, CacheKey(referenceImages, prompt, transparent) + ".png");
        if (!File.Exists(p) || new FileInfo(p).Length == 0) return null;
        if (!transparent) FlattenOpaque(p);
        return p;
    }

    /// <summary>
    /// 部分图像接口会无视提示词直接返回透明底。透明 AI 图既不适合当缩略图，也会让轮廓识别走错分支；
    /// 统一铺成品红键底，后续按色键处理。只处理真正带透明像素的图，正常品红底不做改动。
    /// </summary>
    private static void FlattenOpaque(string path)
    {
        RgbaImage img;
        try { img = RgbaImage.Load(path); }
        catch (Exception) { return; }
        int transparent = 0;
        for (int i = 3; i < img.Px.Length; i += 4)
            if (img.Px[i] < 250) transparent++;
        if (transparent < img.W * img.H / 200) return;
        for (int i = 0; i < img.Px.Length; i += 4)
        {
            int a = img.Px[i + 3];
            if (a == 255) continue;
            int inv = 255 - a;
            img.Px[i] = (byte)((img.Px[i] * a + 255 * inv + 127) / 255);
            img.Px[i + 1] = (byte)((img.Px[i + 1] * a + 127) / 255);
            img.Px[i + 2] = (byte)((img.Px[i + 2] * a + 255 * inv + 127) / 255);
            img.Px[i + 3] = 255;
        }
        try { img.SavePng(path); } catch (Exception) { /* 修复失败也不影响主要流程 */ }
    }

    /// <summary>以参考图编辑生成新图，返回保存路径。命中缓存时直接复制缓存，不调用接口。</summary>
    public async Task<string> EditAsync(IReadOnlyList<string> referenceImages, string prompt, string outPath, CancellationToken ct = default, bool transparent = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var cached = ForceRegenerate ? null : CachedPath(referenceImages, prompt, transparent);
        if (cached != null)
        {
            File.Copy(cached, outPath, overwrite: true);
            LastFromCache = true;
            return outPath;
        }
        LastFromCache = false;
        if (!Configured) throw new InvalidOperationException("尚未配置图像 API（设置 → AI 图像服务：地址、Key 与模型）");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_s.ImageModel), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("1024x1024"), "size");
        // 默认不请求透明底：实测模型有时会把「透明」直接画成灰白棋盘格（浅色物体会被误抠）。
        // 改为要求品红色键底，抠图最稳定；透明底仅作为可选项保留。
        if (transparent)
        {
            form.Add(new StringContent("transparent"), "background");
            form.Add(new StringContent("png"), "output_format");
        }
        foreach (var p in referenceImages)
        {
            var content = new ByteArrayContent(await File.ReadAllBytesAsync(p, ct));
            content.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(p).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            });
            form.Add(content, referenceImages.Count > 1 ? "image[]" : "image", Path.GetFileName(p));
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, EditsUrl(_s.ImageApiBase)) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.ImageApiKey);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"图像 API 返回 {(int)resp.StatusCode}：{Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("data")[0];
        byte[] bytes;
        if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            bytes = Convert.FromBase64String(b64.GetString()!);
        else if (item.TryGetProperty("url", out var url))
            bytes = await Http.GetByteArrayAsync(url.GetString()!, ct);
        else throw new InvalidOperationException("图像 API 返回格式无法识别：" + Trim(body));
        await File.WriteAllBytesAsync(outPath, bytes, ct);
        if (!transparent) FlattenOpaque(outPath);
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.Copy(outPath, Path.Combine(CacheDir, CacheKey(referenceImages, prompt, transparent) + ".png"), overwrite: true);
        }
        catch (IOException) { /* 缓存写失败不影响本次结果 */ }
        return outPath;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
