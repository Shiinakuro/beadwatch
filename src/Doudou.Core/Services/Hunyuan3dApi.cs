using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Doudou.Core.Services;

/// <summary>在线混元 3.1 的附加标准视图。</summary>
public sealed record HunyuanView(string ViewType, string Path);

/// <summary>腾讯混元 3D 在线 API（AI3D 的 OpenAI 兼容接口，Bearer API Key）。</summary>
public sealed class Hunyuan3dApi
{
    public const string DefaultBase = "https://api.ai3d.cloud.tencent.com";
    private readonly string _base;
    private readonly string _key;

    public Hunyuan3dApi(AppSettings s)
    {
        _base = string.IsNullOrWhiteSpace(s.Hunyuan3dApiBase) ? DefaultBase : s.Hunyuan3dApiBase.Trim().TrimEnd('/');
        _key = s.Hunyuan3dApiKey.Trim();
    }

    public bool Configured => _key.Length > 0;

    public Task<string> GenerateAsync(string imagePath, string outDir, IProgress<string>? progress, CancellationToken ct) =>
        GenerateAsync(imagePath, outDir, Array.Empty<HunyuanView>(), progress, ct);

    /// <summary>用正面图生成 3.1 模型；提供附加视图时优先走官方多视图接口，失败自动回退单图。</summary>
    public async Task<string> GenerateAsync(string imagePath, string outDir, IReadOnlyList<HunyuanView> extraViews,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (!Configured)
            throw new InvalidOperationException("未填写混元 3D 在线 API Key。请到「设置 → 混元 3D 在线 API」填写。");
        Directory.CreateDirectory(outDir);
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
            BaseAddress = new Uri(_base.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _key);

        var front = Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath, ct));
        var views = extraViews.Where(v => File.Exists(v.Path)).Take(5).ToList();
        List<Dictionary<string, string>>? BuildViews(string field) => views.Count == 0
            ? null
            : views.Select(v => new Dictionary<string, string>
            {
                ["ViewType"] = v.ViewType,
                [field] = Convert.ToBase64String(File.ReadAllBytes(v.Path)),
            }).ToList();

        object Body(List<Dictionary<string, string>>? multiViews) => multiViews == null
            ? new { Model = "3.1", ImageBase64 = front }
            : new { Model = "3.1", ImageBase64 = front, MultiViewImages = multiViews };

        var multi = BuildViews("ViewImageBase64");
        progress?.Report(multi != null ? $"提交混元 3.1 在线任务（{views.Count + 1} 个视图）…" : "提交混元 3.1 在线任务…");
        var (job, submitText) = await SubmitAsync(http, Body(multi), ct);
        if (job == null && multi != null && LooksLikeViewParameterError(submitText))
        {
            progress?.Report("尝试兼容的多视图字段…");
            (job, submitText) = await SubmitAsync(http, Body(BuildViews("ImageBase64")), ct);
        }
        if (job == null && multi != null && LooksLikeViewParameterError(submitText))
        {
            progress?.Report("多视图参数未被接口接受，回退单图重试…");
            (job, submitText) = await SubmitAsync(http, Body(null), ct);
        }
        if (string.IsNullOrWhiteSpace(job))
            throw new InvalidOperationException("混元 API 提交失败：" + Clip(submitText));

        progress?.Report("任务已提交，等待生成…");
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(3000, ct);
            var queryBody = JsonSerializer.Serialize(new { JobId = job });
            using var query = await http.PostAsync(_base + "/v1/ai3d/query",
                new StringContent(queryBody, Encoding.UTF8, "application/json"), ct);
            var queryText = await query.Content.ReadAsStringAsync(ct);
            using var qdoc = JsonDocument.Parse(queryText);
            if (!qdoc.RootElement.TryGetProperty("Response", out var qr))
                throw new InvalidOperationException("混元 API 查询异常：" + Clip(queryText));
            ThrowIfError(qr, query.StatusCode);
            var status = qr.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";
            progress?.Report(status switch
            {
                "RUN" => $"混元 3.1 正在生成…（{(i + 1) * 3}s）",
                "DONE" => "混元 3.1 生成完成，下载模型…",
                _ => $"混元 3.1 状态：{status}",
            });
            if (status.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                return await DownloadResultAsync(http, qr, outDir, ct);
            if (status is "FAIL" or "FAILED" or "ERROR")
                throw new InvalidOperationException("混元 3.1 生成失败：" + Clip(queryText));
        }
        throw new TimeoutException("混元 3.1 生成超时（10 分钟）。");
    }

    private static async Task<(string? Job, string Text)> SubmitAsync(HttpClient http, object body, CancellationToken ct)
    {
        using var submit = await http.PostAsync(new Uri(http.BaseAddress!, "v1/ai3d/submit"),
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        var text = await submit.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("Response", out var sr)) return (null, text);
            if (sr.TryGetProperty("Error", out var err))
            {
                var code = err.TryGetProperty("Code", out var c) ? c.GetString() ?? "" : "";
                var msg = err.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "";
                if (code.Length > 0) return (null, $"{code} {msg}".Trim());
            }
            var job = sr.TryGetProperty("JobId", out var j) ? j.GetString() : null;
            return (string.IsNullOrWhiteSpace(job) ? null : job, text);
        }
        catch (JsonException)
        {
            return (null, text);
        }
    }

    private static bool LooksLikeViewParameterError(string text) =>
        text.Contains("MultiView", StringComparison.OrdinalIgnoreCase)
        || text.Contains("ViewImage", StringComparison.OrdinalIgnoreCase)
        || text.Contains("ViewType", StringComparison.OrdinalIgnoreCase)
        || text.Contains("InvalidParameter", StringComparison.OrdinalIgnoreCase)
        || text.Contains("参数", StringComparison.Ordinal);

    /// <summary>用不存在的 JobId 查询，验证 Key 是否有效；不会创建任务或扣积分。</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string baseUrl, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return (false, "请先填写 API Key。");
        var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl.Trim().TrimEnd('/');
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var req = new HttpRequestMessage(HttpMethod.Post, b + "/v1/ai3d/query")
            {
                Content = new StringContent("{\"JobId\":\"0\"}", Encoding.UTF8, "application/json"),
            };
            using var r = await http.SendAsync(req, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            if (r.StatusCode == HttpStatusCode.Unauthorized) return (false, "Key 无效或没有权限（401）。");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Response", out var resp) && resp.TryGetProperty("Error", out var err))
            {
                var code = err.TryGetProperty("Code", out var c) ? c.GetString() ?? "" : "";
                var msg = err.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "";
                if (code.Contains("JobNotFound", StringComparison.OrdinalIgnoreCase)) return (true, "Key 有效，接口已连通。");
                if (code.Contains("Auth", StringComparison.OrdinalIgnoreCase)) return (false, msg.Length > 0 ? msg : "鉴权失败。");
                return (true, "接口已连通（返回：" + code + "）。");
            }
            return (true, "接口已连通。");
        }
        catch (Exception e)
        {
            return (false, "连接失败：" + e.Message);
        }
    }

    private static async Task<string> DownloadResultAsync(HttpClient http, JsonElement response, string outDir, CancellationToken ct)
    {
        if (!response.TryGetProperty("ResultFile3Ds", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("混元 API 没有返回模型文件。");
        string? glb = null, obj = null;
        foreach (var f in files.EnumerateArray())
        {
            var type = f.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            var url = f.TryGetProperty("Url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (type.Equals("GLB", StringComparison.OrdinalIgnoreCase)) glb = url;
            else if (type.Equals("OBJ", StringComparison.OrdinalIgnoreCase)) obj = url;
        }
        var targetUrl = glb ?? obj ?? throw new InvalidOperationException("混元 API 返回的文件类型不受支持。");
        var data = await http.GetByteArrayAsync(targetUrl, ct);
        if (glb != null)
        {
            var path = Path.Combine(outDir, "hunyuan3d.glb");
            await File.WriteAllBytesAsync(path, data, ct);
            return path;
        }
        var zipPath = Path.Combine(outDir, "hunyuan3d_obj.zip");
        await File.WriteAllBytesAsync(zipPath, data, ct);
        var extract = Path.Combine(outDir, "obj");
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(zipPath, extract, true);
        var glbFile = Directory.EnumerateFiles(extract, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
        if (glbFile != null) return glbFile;
        var objFile = Directory.EnumerateFiles(extract, "*.obj", SearchOption.AllDirectories).FirstOrDefault();
        return objFile ?? throw new InvalidOperationException("下载的 OBJ 压缩包里没有 .obj/.glb 文件。");
    }

    private static void ThrowIfError(JsonElement response, HttpStatusCode code)
    {
        if (!response.TryGetProperty("Error", out var err)) return;
        var c = err.TryGetProperty("Code", out var cc) ? cc.GetString() ?? "" : "";
        var m = err.TryGetProperty("Message", out var mm) ? mm.GetString() ?? "" : "";
        if (c.Length > 0) throw new InvalidOperationException($"混元 API 错误（{(int)code}）：{c} {m}");
    }

    private static string Clip(string s) => s.Length > 400 ? s[..400] : s;
}
