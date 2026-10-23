using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Doudou.Core.Services;

/// <summary>
/// 混元3D 本地工作进程。首次调用时启动（加载模型约 20~30 秒），之后常驻复用，程序退出时关闭。
/// </summary>
public sealed class Hy3dClient : IDisposable
{
    private Process? _proc;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId = 1;
    private readonly StringBuilder _stderr = new();

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>本地抠图模型是否可用（不需要显卡）。</summary>
    public static bool CutoutInstalled
    {
        get
        {
            var model = Path.Combine(AppPaths.Models, "u2net", "isnet-general-use.onnx");
            return File.Exists(AppPaths.Python) && File.Exists(AppPaths.Hy3dWorker) && HasPackage("onnxruntime") &&
                   HasPackage("rembg") && File.Exists(model) && new FileInfo(model).Length > 100_000_000;
        }
    }

    public static bool IsInstalled(out string reason)
    {
        if (!File.Exists(AppPaths.Python)) { reason = "未安装本地 AI 运行时"; return false; }
        if (!File.Exists(AppPaths.Hy3dWorker)) { reason = "缺少本地 AI 工作脚本"; return false; }
        if (!HasPackage("torch") || !HasPackage("diffusers") || !HasPackage("transformers"))
        {
            reason = "本地 AI 运行库不完整";
            return false;
        }
        var mv = Path.Combine(AppPaths.Models, "tencent", "Hunyuan3D-2mv", "hunyuan3d-dit-v2-mv-turbo", "model.fp16.safetensors");
        if (!File.Exists(mv) || new FileInfo(mv).Length < 4_000_000_000) { reason = "未安装混元3D-2mv 形状模型"; return false; }
        var vae = Path.Combine(AppPaths.Models, "tencent", "Hunyuan3D-2", "hunyuan3d-vae-v2-0-turbo", "model.fp16.safetensors");
        if (!File.Exists(vae) || new FileInfo(vae).Length < 300_000_000) { reason = "未安装混元3D VAE"; return false; }
        reason = "";
        return true;
    }

    private static bool HasPackage(string name)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.Python);
            return dir != null && Directory.Exists(Path.Combine(dir, "Lib", "site-packages", name));
        }
        catch (Exception) { return false; }
    }

    private async Task StartAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsRunning) return;
        progress?.Report("启动本地 AI 进程…");
        var psi = new ProcessStartInfo(AppPaths.Python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(AppPaths.Hy3dWorker)!,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(AppPaths.Hy3dWorker);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["HY3DGEN_MODELS"] = AppPaths.Models;
        psi.Environment["U2NET_HOME"] = Path.Combine(AppPaths.Models, "u2net");
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        var pyDir = Path.GetDirectoryName(AppPaths.Python)!;
        psi.Environment["PATH"] = string.Join(';', pyDir, Path.Combine(pyDir, "Library", "bin"), Path.Combine(pyDir, "Scripts"),
            Environment.GetEnvironmentVariable("PATH") ?? "");
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Python");
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 20000) _stderr.Remove(0, _stderr.Length - 20000);
            }
        };
        _proc.BeginErrorReadLine();

        var ready = await ReadEventAsync(ct, TimeSpan.FromMinutes(5));
        if (ready?["event"]?.GetValue<string>() != "ready")
        {
            var err = ready?["error"]?.GetValue<string>() ?? LastStderr();
            Kill();
            throw new InvalidOperationException("本地 AI 进程启动失败：" + err);
        }
    }

    private async Task<JsonNode?> ReadEventAsync(CancellationToken ct, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            var line = await _proc!.StandardOutput.ReadLineAsync(cts.Token);
            if (line == null) return null;
            line = line.Trim();
            if (!line.StartsWith('{')) continue;
            try { return JsonNode.Parse(line); } catch (JsonException) { /* 非协议输出 */ }
        }
    }

    public string LastStderr()
    {
        lock (_stderr)
        {
            var s = _stderr.ToString().Trim();
            return s.Length > 1500 ? s[^1500..] : s;
        }
    }

    /// <summary>由多视图生成网格（.obj）。views 的键为 front/left/back/right，值为去背景 RGBA PNG 路径。</summary>
    public async Task<string> GenerateAsync(IDictionary<string, string> views, string outObj, int steps = 5, int octree = 256, int seed = 1234,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var req = new JsonObject
        {
            ["cmd"] = "shape",
            ["views"] = new JsonObject(views.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["out"] = outObj,
            ["steps"] = steps,
            ["octree"] = octree,
            ["seed"] = seed,
        };
        var ev = await RequestAsync(req, "混元3D", TimeSpan.FromMinutes(10), progress, ct);
        return ev["out"]!.GetValue<string>();
    }

    /// <summary>
    /// 主体抠图：先按 box（原图像素坐标 x0,y0,x1,y1）裁剪，再用本地 ISNet 去背景，输出透明底 PNG。
    /// </summary>
    public async Task<string> CutoutAsync(string input, string outPng, (int X0, int Y0, int X1, int Y1)? box,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var req = new JsonObject { ["cmd"] = "cutout", ["in"] = input, ["out"] = outPng };
        if (box is { } b) req["box"] = new JsonArray(b.X0, b.Y0, b.X1, b.Y1);
        var ev = await RequestAsync(req, "抠图", TimeSpan.FromMinutes(3), progress, ct);
        return ev["out"]!.GetValue<string>();
    }

    private async Task<JsonNode> RequestAsync(JsonObject req, string what, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(AppPaths.Python) || !File.Exists(AppPaths.Hy3dWorker))
                throw new InvalidOperationException("未安装本地 AI 运行时，请在「设置」中一键安装依赖");
            await StartAsync(progress, ct);
            int id = _nextId++;
            req["id"] = id;
            await _proc!.StandardInput.WriteLineAsync(req.ToJsonString());
            await _proc.StandardInput.FlushAsync();
            while (true)
            {
                var ev = await ReadEventAsync(ct, timeout);
                if (ev == null) throw new InvalidOperationException($"本地 AI 进程意外退出：" + LastStderr());
                var kind = ev["event"]?.GetValue<string>();
                if (kind == "progress") { progress?.Report($"{what}：" + ev["stage"]?.GetValue<string>()); continue; }
                if (kind == "done") return ev;
                if (kind == "error") throw new InvalidOperationException($"{what}失败：" + ev["error"]?.GetValue<string>());
            }
        }
        catch (OperationCanceledException)
        {
            Kill();   // 取消时直接结束进程，避免显存被占
            throw;
        }
        finally { _lock.Release(); }
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch (Exception) { }
        _proc = null;
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.StandardInput.WriteLine("{\"quit\": true}");
                if (!_proc.WaitForExit(3000)) _proc.Kill(true);
            }
        }
        catch (Exception) { }
        _proc = null;
    }
}
