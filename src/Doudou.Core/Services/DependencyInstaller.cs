using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Doudou.Core.Services;

public sealed record DepProgress(string Stage, double Percent, string Detail);

/// <summary>
/// 本机 AI 依赖安装：Python 运行时、PyTorch/CUDA、混元3D 权重与 ISNet 抠图模型。
/// 清单优先从官网读取，失败时用内置清单；大文件从国内镜像下载并可断点续传。
/// </summary>
public sealed class DependencyInstaller
{
    public const string ManifestUrl = "https://shiinakuro.github.io/beadwatch/deps/manifest.json";
    private const string LegacyManifestUrl = "https://beadwatch-7c28q17g8xc.qoder.zone/deps/manifest.json";
    private const string PythonDirName = "python";
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CancellationTokenSource? _active;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static bool IsRunning => Gate.CurrentCount == 0;
    public static bool PythonReady => File.Exists(AppPaths.Python);
    public static bool CutoutReady => Hy3dClient.CutoutInstalled;
    public static bool Hy3dReady => Hy3dClient.IsInstalled(out _);

    public static void Cancel()
    {
        try { _active?.Cancel(); } catch (Exception) { }
    }

    public static async Task InstallAsync(bool cutoutOnly, IProgress<DepProgress> progress, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) throw new InvalidOperationException("依赖安装已在进行中");
        _active = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _active.Token;
        try
        {
            Report(progress, "准备", 0, "读取依赖清单…");
            var m = await LoadManifestAsync(token);
            EnsureWritable();
            EnsureDisk(cutoutOnly ? 1_500_000_000 : 15_000_000_000);

            await EnsurePythonAsync(m, progress, token);
            await EnsurePipAsync(m, progress, token);

            if (cutoutOnly)
            {
                await PipPackagesAsync(m, m.CutoutPackages, 8, 18, "安装抠图运行库", progress, token);
                await DownloadCutoutAsync(m, 18, 97, progress, token);
            }
            else
            {
                await InstallTorchAsync(m, progress, token);
                await PipPackagesAsync(m, m.FullPackages, 42, 52, "安装三维运行库", progress, token);
                await DownloadCutoutAsync(m, 52, 60, progress, token);
                await DownloadModelsAsync(m, 60, 97, progress, token);
            }

            await VerifyAsync(cutoutOnly, progress, token);
            Report(progress, "完成", 100, cutoutOnly ? "本地抠图已就绪。" : "本地混元3D 与抠图已就绪。");
        }
        finally
        {
            _active.Dispose();
            _active = null;
            Gate.Release();
        }
    }

    public static async Task<DepManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        foreach (var url in new[] { ManifestUrl, LegacyManifestUrl })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var json = await Http.GetStringAsync(url, cts.Token);
                var m = JsonSerializer.Deserialize<DepManifest>(json, Json);
                if (m is { Schema: 1, Python: not null, Cutout: not null, Torch: not null }) return m;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* 站点不可达时尝试下一入口，最后用内置清单 */ }
        }

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Doudou.Core.Services.deps-manifest.json");
        if (stream != null)
        {
            var m = JsonSerializer.Deserialize<DepManifest>(stream, Json);
            if (m != null) return m;
        }
        var local = Path.Combine(AppContext.BaseDirectory, "deps-manifest.json");
        if (File.Exists(local))
        {
            var m = JsonSerializer.Deserialize<DepManifest>(File.ReadAllText(local), Json);
            if (m != null) return m;
        }
        throw new InvalidOperationException("找不到依赖清单，请重新安装程序。");
    }

    private static async Task EnsurePythonAsync(DepManifest m, IProgress<DepProgress> p, CancellationToken ct)
    {
        if (File.Exists(AppPaths.Python))
        {
            Report(p, "Python", 4, "已检测到 Python 运行时，跳过下载。");
            return;
        }
        var dir = Path.GetDirectoryName(AppPaths.Python)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(Downloads, "python-3.11.9-embed-amd64.zip");
        Report(p, "Python", 1, $"下载 Python {m.Python.Version} 便携运行时（约 11 MB）");
        await DownloadAsync(m.Python, zip, (done, total, speed) => MapProgress(p, "Python", 1, 4, done, total, speed), ct);
        Report(p, "Python", 3, "解压 Python 运行时…");
        ZipFile.ExtractToDirectory(zip, dir, true);
        File.Delete(zip);
        var pth = Path.Combine(dir, "python311._pth");
        File.WriteAllText(pth, "python311.zip\r\n.\r\nLib\\site-packages\r\nimport site\r\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(dir, "Lib", "site-packages"));
        Report(p, "Python", 4, "Python 运行时就绪。");
    }

    private static async Task EnsurePipAsync(DepManifest m, IProgress<DepProgress> p, CancellationToken ct)
    {
        var check = await RunAsync(AppPaths.Python, new[] { "-m", "pip", "--version" }, ct);
        if (check.Code == 0)
        {
            Report(p, "pip", 8, "pip 已就绪。");
            return;
        }
        Report(p, "pip", 5, "下载 pip 引导脚本…");
        var script = Path.Combine(Downloads, "get-pip.py");
        await DownloadAsync(m.GetPip, script, (done, total, speed) => MapProgress(p, "pip", 5, 7, done, total, speed), ct);
        Report(p, "pip", 7, "安装 pip（约 1 分钟）…");
        var result = await PipInstallRawAsync(m, script, new[] { "--no-warn-script-location" }, ct);
        File.Delete(script);
        if (result.Code != 0) throw new InvalidOperationException("pip 安装失败：" + ResultTail(result));
        check = await RunAsync(AppPaths.Python, new[] { "-m", "pip", "--version" }, ct);
        if (check.Code != 0) throw new InvalidOperationException("pip 安装后仍无法运行：" + ResultTail(check));
        Report(p, "pip", 8, "pip 就绪。");
    }

    private static async Task<TorchSpec> SelectTorchAsync(DepManifest m, CancellationToken ct)
    {
        if (m.Torch.Variants.Count == 0) return m.Torch;
        double maxCompute = 0;
        var candidates = new[]
        {
            "nvidia-smi",
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        };
        foreach (var exe in candidates)
        {
            try
            {
                var r = await RunAsync(exe, new[] { "--query-gpu=compute_cap", "--format=csv,noheader" }, ct);
                if (r.Code != 0) continue;
                foreach (var raw in r.Tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap))
                        maxCompute = Math.Max(maxCompute, cap);
                if (maxCompute > 0) break;
            }
            catch (Exception) { }
        }
        if (maxCompute <= 0) return m.Torch;
        return m.Torch.Variants
            .Where(v => maxCompute + 1e-6 >= v.MinCompute)
            .OrderByDescending(v => v.MinCompute)
            .FirstOrDefault() ?? m.Torch;
    }

    private static int CudaTag(string version)
    {
        int i = version.IndexOf("+cu", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 0;
        int j = i + 3, k = j;
        while (k < version.Length && char.IsDigit(version[k])) k++;
        return k > j && int.TryParse(version[j..k], out var n) ? n : 0;
    }

    private static bool Covers(string actual, TorchSpec expected)
    {
        var parts = actual.Split('|');
        if (parts.Length != 2) return false;
        if (string.Equals(parts[0], expected.Version, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], expected.TorchvisionVersion, StringComparison.OrdinalIgnoreCase))
            return true;
        // 已装的 CUDA 构建比目标新（例如老卡上已有 cu128）时不要降级。
        int at = CudaTag(parts[0]), av = CudaTag(parts[1]);
        int et = CudaTag(expected.Version), ev = CudaTag(expected.TorchvisionVersion);
        return et > 0 && ev > 0 && at >= et && av >= ev;
    }

    private static async Task InstallTorchAsync(DepManifest m, IProgress<DepProgress> p, CancellationToken ct)
    {
        var torch = await SelectTorchAsync(m, ct);
        var current = await RunAsync(AppPaths.Python, new[]
        {
            "-c", "import torch, torchvision; print(torch.__version__ + '|' + torchvision.__version__)"
        }, ct);
        if (current.Code == 0)
        {
            var actual = current.Tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.Contains('|'));
            if (actual != null && Covers(actual, torch))
            {
                Report(p, "PyTorch", 41, $"已检测到可用的 PyTorch {actual.Replace('|', '/')}，跳过下载。");
                return;
            }
            if (!string.IsNullOrEmpty(actual))
                Report(p, "PyTorch", 8, $"检测到 PyTorch {actual.Replace('|', '/')}，将使用 {torch.Name}。");
        }

        Report(p, "PyTorch", 8, $"安装 {torch.Name} 基础依赖…");
        var deps = await PipAsync(m, m.TorchDeps, ct);
        if (deps.Code != 0) throw new InvalidOperationException("PyTorch 基础依赖安装失败：" + ResultTail(deps));

        var torchFile = Path.Combine(Downloads, $"torch-{torch.Version}-cp311-cp311-win_amd64.whl");
        var visionFile = Path.Combine(Downloads, $"torchvision-{torch.TorchvisionVersion}-cp311-cp311-win_amd64.whl");
        Report(p, "PyTorch", 9, $"下载 PyTorch CUDA（约 {torch.Torch.Size / 1073741824.0:F1} GB，可断点续传）");
        await DownloadAsync(torch.Torch, torchFile, (done, total, speed) => MapProgress(p, "PyTorch", 9, 38, done, total, speed), ct);
        Report(p, "PyTorch", 38, "下载 TorchVision…");
        await DownloadAsync(torch.Torchvision, visionFile, (done, total, speed) => MapProgress(p, "PyTorch", 38, 40, done, total, speed), ct);

        Report(p, "PyTorch", 40, "安装 PyTorch CUDA（约 3–8 分钟）…");
        var install = await RunPipAsync(m, new[] { "install", "--no-deps", "--force-reinstall", torchFile, visionFile }, ct);
        if (install.Code != 0) throw new InvalidOperationException("PyTorch 安装失败：" + ResultTail(install));
        var verify = await RunAsync(AppPaths.Python, new[] { "-c", "import torch; print(torch.__version__)" }, ct);
        if (verify.Code != 0 || !verify.Tail.Contains(torch.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PyTorch 版本未更新到 " + torch.Version + "：" + ResultTail(verify));
        try { File.Delete(torchFile); } catch (Exception) { }
        try { File.Delete(visionFile); } catch (Exception) { }
        Report(p, "PyTorch", 42, $"{torch.Name} 就绪。");
    }

    private static async Task PipPackagesAsync(DepManifest m, IReadOnlyList<string> packages, double from, double to,
        string stage, IProgress<DepProgress> p, CancellationToken ct)
    {
        Report(p, stage, from, "解析并下载运行库（自动选择 PyPI 镜像，首次可能需要几分钟）…");
        static string Clip(string s) => s.Length > 140 ? s[..137] + "…" : s;
        void Line(string raw)
        {
            var s = raw.Trim();
            if (s.Length == 0) return;
            if (s.StartsWith("Collecting ", StringComparison.Ordinal))
                Report(p, stage, from, "解析依赖：" + Clip(s[11..]));
            else if (s.StartsWith("Downloading ", StringComparison.Ordinal))
                Report(p, stage, from, "下载：" + Clip(s[12..]));
            else if (s.StartsWith("Installing collected packages", StringComparison.Ordinal))
                Report(p, stage, from + (to - from) * 0.8, "安装运行库…");
            else if (s.StartsWith("Successfully installed", StringComparison.Ordinal))
                Report(p, stage, to, "运行库安装完成。");
        }
        var result = await PipAsync(m, packages, ct, Line, index =>
        {
            try { Report(p, stage, from, $"PyPI 镜像：{new Uri(index).Host}"); }
            catch (UriFormatException) { Report(p, stage, from, "PyPI 镜像：" + index); }
        });
        if (result.Code != 0) throw new InvalidOperationException(stage + "失败：" + ResultTail(result));
        Report(p, stage, to, "运行库安装完成。");
    }

    private static async Task DownloadCutoutAsync(DepManifest m, double from, double to, IProgress<DepProgress> p, CancellationToken ct)
    {
        var c = m.Cutout;
        var final = Path.Combine(AppPaths.Models, c.Path.Replace('/', Path.DirectorySeparatorChar));
        if (IsComplete(final, c.Size))
        {
            Report(p, "抠图模型", to, "已检测到 ISNet 抠图模型，跳过下载。");
            return;
        }
        Report(p, "抠图模型", from, "下载 ISNet 抠图模型（约 170 MB）");
        await DownloadAsync(c, final, (done, total, speed) => MapProgress(p, "抠图模型", from, to, done, total, speed), ct);
        Report(p, "抠图模型", to, "ISNet 抠图模型就绪。");
    }

    private static async Task DownloadModelsAsync(DepManifest m, double from, double to, IProgress<DepProgress> p, CancellationToken ct)
    {
        long total = m.Models.Sum(x => x.Size);
        long baseDone = 0;
        foreach (var model in m.Models)
        {
            var final = Path.Combine(AppPaths.Models, model.Path.Replace('/', Path.DirectorySeparatorChar));
            if (IsComplete(final, model.Size))
            {
                WriteConfig(model);
                baseDone += model.Size;
                Report(p, "混元3D", from + (to - from) * baseDone / Math.Max(1, total), model.Name + "已就绪。");
                continue;
            }
            long offset = baseDone;
            Report(p, "混元3D", from + (to - from) * offset / Math.Max(1, total), $"下载 {model.Name}（{FormatSize(model.Size)}，可断点续传）");
            await DownloadAsync(model, final, (done, t, speed) =>
                MapProgress(p, "混元3D", from, to, offset + done, total, speed), ct);
            WriteConfig(model);
            baseDone += model.Size;
        }
        Report(p, "混元3D", to, "混元3D 权重已就绪。");
    }

    private static void WriteConfig(ModelSpec model)
    {
        if (model.Config == null || string.IsNullOrWhiteSpace(model.ConfigPath)) return;
        var path = Path.Combine(AppPaths.Models, model.ConfigPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, model.Config, new UTF8Encoding(false));
    }

    private static async Task VerifyAsync(bool cutoutOnly, IProgress<DepProgress> p, CancellationToken ct)
    {
        Report(p, "自检", 97, "正在导入运行库（首次需要 20–60 秒）…");
        var runtime = Path.Combine(AppPaths.Root, "runtime");
        var code = cutoutOnly
            ? "import onnxruntime, rembg, numpy, PIL; print('ok')"
            : "import sys; sys.path.insert(0, r'" + runtime.Replace("'", "''") + "'); import torch, torchvision, diffusers, transformers, pymeshlab, trimesh, onnxruntime, rembg, hy3dgen.shapegen; print(torch.__version__)";
        var check = await RunAsync(AppPaths.Python, new[] { "-c", code }, ct);
        if (check.Code != 0) throw new InvalidOperationException("运行库自检失败：" + ResultTail(check));
        if (!cutoutOnly)
        {
            if (!Hy3dClient.IsInstalled(out var why)) throw new InvalidOperationException("安装完成但自检未通过：" + why);
            Report(p, "自检", 99, "运行库与模型自检通过。");
        }
        else if (!Hy3dClient.CutoutInstalled) throw new InvalidOperationException("安装完成但未找到 ISNet 抠图模型。");
        Report(p, "自检", 100, "自检通过。");
    }

    private static async Task<RunResult> PipAsync(DepManifest m, IReadOnlyList<string> packages, CancellationToken ct,
        Action<string>? onLine = null, Action<string>? onIndex = null)
    {
        var constraints = Path.Combine(AppPaths.Root, "runtime", "pip-constraints.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(constraints)!);
        await File.WriteAllLinesAsync(constraints, m.Constraints, new UTF8Encoding(false), ct);
        return await RunPipAsync(m, new[] { "install", "-c", constraints }.Concat(packages), ct, onLine, onIndex);
    }

    private static async Task<RunResult> PipInstallRawAsync(DepManifest m, string script, IEnumerable<string> extra, CancellationToken ct)
    {
        var index = m.Indexes.FirstOrDefault() ?? "https://pypi.org/simple";
        var args = new List<string> { script, "-i", index, "--disable-pip-version-check", "--no-cache-dir" };
        args.AddRange(extra);
        var r = await RunAsync(AppPaths.Python, args, ct, PipEnv());
        if (r.Code != 0 && m.Indexes.Count > 1)
        {
            args[2] = m.Indexes[1];
            r = await RunAsync(AppPaths.Python, args, ct, PipEnv());
        }
        return r;
    }

    private static readonly TimeSpan PipIndexTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PipInactivityTimeout = TimeSpan.FromMinutes(4);

    private sealed class ActivityClock
    {
        private long _ticks = Environment.TickCount64;
        public void Touch() => Interlocked.Exchange(ref _ticks, Environment.TickCount64);
        public bool Stale(TimeSpan span) =>
            Environment.TickCount64 - Interlocked.Read(ref _ticks) > (long)span.TotalMilliseconds;
    }

    private static async Task<RunResult> RunPipAsync(DepManifest m, IEnumerable<string> installArgs, CancellationToken ct,
        Action<string>? onLine = null, Action<string>? onIndex = null)
    {
        var args = new List<string> { "-m", "pip" };
        args.AddRange(installArgs);
        args.AddRange(new[]
        {
            "--disable-pip-version-check", "--no-input", "--no-warn-script-location",
            "--no-cache-dir", "--progress-bar", "off", "--prefer-binary",
            "--timeout", "20", "--retries", "1",
        });
        RunResult last = new(1, "未执行");
        foreach (var index in m.Indexes)
        {
            ct.ThrowIfCancellationRequested();
            onIndex?.Invoke(index);
            var a = new List<string>(args) { "-i", index };
            var clock = new ActivityClock();
            using var indexCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            indexCts.CancelAfter(PipIndexTimeout);
            using var watchdog = new System.Threading.Timer(_ =>
            {
                if (!clock.Stale(PipInactivityTimeout)) return;
                try { indexCts.Cancel(); } catch (ObjectDisposedException) { }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            try
            {
                last = await RunAsync(AppPaths.Python, a, indexCts.Token, PipEnv(), line =>
                {
                    clock.Touch();
                    onLine?.Invoke(line);
                });
                if (last.Code == 0) return last;
                onLine?.Invoke("当前镜像未完成，尝试下一个源…");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new RunResult(-1, "镜像响应超时");
                onLine?.Invoke("镜像长时间无响应，切换下一个源…");
            }
        }
        return last;
    }

    private static Dictionary<string, string> PipEnv()
    {
        var tmp = Path.Combine(AppPaths.Root, "runtime", "tmp");
        Directory.CreateDirectory(tmp);
        return new()
        {
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONNOUSERSITE"] = "1",
            ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            ["PIP_NO_INPUT"] = "1",
            ["TEMP"] = tmp,
            ["TMP"] = tmp,
        };
    }

    private const long SpeedTestMinSize = 4L << 20;
    private const long SpeedSampleBytes = 1L << 20;
    private const long SegmentThreshold = 32L << 20;
    private const long SegmentChunkSize = 16L << 20;
    private const double SegmentSpeedLimit = 12d * 1024 * 1024;

    private sealed record MirrorCandidate(string Url, double BytesPerSecond);

    private sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException() : base("服务器不支持分段下载") { }
    }

    private sealed class SegmentState
    {
        public int Version { get; set; }
        public long Size { get; set; }
        public long ChunkSize { get; set; }
        public List<int> Completed { get; set; } = new();
    }

    /// <summary>并发采样各镜像的首 MB，按实测速度排序；慢源会排在后面。</summary>
    private static async Task<List<MirrorCandidate>> RankMirrorsAsync(FileSpec spec, CancellationToken ct)
    {
        var urls = spec.Urls.Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return new();
        if (spec.Size < SpeedTestMinSize)
            return urls.Select(u => new MirrorCandidate(u, 0)).ToList();

        var tasks = urls.Select(async url =>
        {
            long got = 0;
            var watch = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                long end = Math.Min(SpeedSampleBytes, spec.Size > 0 ? spec.Size : SpeedSampleBytes) - 1;
                req.Headers.Range = new RangeHeaderValue(0, Math.Max(0, end));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return new MirrorCandidate(url, 0);
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[64 * 1024];
                while (got < SpeedSampleBytes)
                {
                    int n = await stream.ReadAsync(buffer, cts.Token);
                    if (n <= 0) break;
                    got += n;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 采样超时，按已收到的字节算速度 */ }
            catch (Exception) when (!ct.IsCancellationRequested) { /* 采样失败，排到最后 */ }
            var seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            return new MirrorCandidate(url, got / seconds);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var ranked = results.OrderByDescending(x => x.BytesPerSecond).ToList();
        return ranked.Any(x => x.BytesPerSecond > 0)
            ? ranked
            : urls.Select(u => new MirrorCandidate(u, 0)).ToList();
    }

    private static async Task DownloadAsync(FileSpec spec, string finalPath, Action<long, long, double>? onProgress, CancellationToken ct)
    {
        if (IsComplete(finalPath, spec.Size)) return;
        if (File.Exists(finalPath)) File.Delete(finalPath);
        var part = finalPath + ".part";
        var state = part + ".state.json";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var ranked = await RankMirrorsAsync(spec, ct);
        if (ranked.Count == 0) throw new IOException($"下载失败：{spec.Name} 没有可用地址。");

        bool hasSegments = spec.Size >= SegmentThreshold && File.Exists(state);
        bool segmented = spec.Size >= SegmentThreshold &&
                         (hasSegments || (ranked[0].BytesPerSecond > 0 && ranked[0].BytesPerSecond < SegmentSpeedLimit));
        if (segmented)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SegmentedDownloadAsync(ranked, part, state, spec, 4, onProgress, ct);
                    if (!TryFinish(part, state, finalPath, spec))
                    {
                        if (attempt == 2) throw new IOException($"{spec.Name} 校验失败。");
                        await Task.Delay(800 * attempt, ct);
                        continue;
                    }
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (RangeNotSupportedException)
                {
                    TryDelete(part);
                    TryDelete(state);
                    break;
                }
                catch (Exception e)
                {
                    if (attempt == 2) throw new IOException($"下载失败：{spec.Name}。{e.Message}", e);
                    await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt), ct);
                }
            }
        }

        Exception? lastError = null;
        foreach (var mirror in ranked)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadOneAsync(mirror.Url, part, spec.Size, onProgress, ct);
                    if (!TryFinish(part, state, finalPath, spec)) throw new IOException($"{spec.Name} 校验失败。");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    lastError = e;
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), ct);
                }
            }
        }
        throw new IOException($"下载失败：{spec.Name}。{lastError?.Message}", lastError);
    }

    private static async Task SegmentedDownloadAsync(IReadOnlyList<MirrorCandidate> mirrors, string part, string statePath,
        FileSpec spec, int requestedConnections, Action<long, long, double>? onProgress, CancellationToken ct)
    {
        long size = spec.Size;
        long chunkSize = SegmentChunkSize;
        int chunkCount = (int)((size + chunkSize - 1) / chunkSize);
        var completed = new HashSet<int>();
        bool resume = false;

        if (File.Exists(part) && new FileInfo(part).Length == size && File.Exists(statePath))
        {
            try
            {
                var st = JsonSerializer.Deserialize<SegmentState>(File.ReadAllText(statePath));
                if (st is { Version: 1 } && st.Size == size && st.ChunkSize == chunkSize)
                {
                    foreach (var i in st.Completed)
                        if (i >= 0 && i < chunkCount) completed.Add(i);
                    resume = true;
                }
            }
            catch (Exception) { }
        }
        if (!resume)
        {
            TryDelete(part);
            TryDelete(statePath);
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read))
                fs.SetLength(size);
        }

        var gate = new object();
        void SaveStateLocked()
        {
            try
            {
                var st = new SegmentState
                {
                    Version = 1, Size = size, ChunkSize = chunkSize,
                    Completed = completed.OrderBy(x => x).ToList(),
                };
                var tmp = statePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(st));
                File.Move(tmp, statePath, true);
            }
            catch (Exception) { /* 状态写不进去也不影响本次下载 */ }
        }

        long LengthOf(int index) => Math.Min(chunkSize, size - (long)index * chunkSize);
        long completedBytes = completed.Sum(LengthOf);
        long bytesDone = completedBytes;
        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, chunkCount).Where(i => !completed.Contains(i)));
        var meter = new DownloadMeter(completedBytes, size, onProgress);

        using var handle = File.OpenHandle(part, FileMode.Open, FileAccess.Write, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? firstError = null;

        async Task Worker()
        {
            var token = linked.Token;
            while (queue.TryDequeue(out var index))
            {
                token.ThrowIfCancellationRequested();
                long offset = (long)index * chunkSize;
                int length = (int)LengthOf(index);
                Exception? chunkError = null;
                bool ok = false;
                foreach (var mirror in mirrors)
                {
                    for (int attempt = 1; attempt <= 2 && !ok; attempt++)
                    {
                        long attemptBytes = 0;
                        try
                        {
                            await DownloadRangeAsync(mirror.Url, handle, offset, length, n =>
                            {
                                attemptBytes += n;
                                var done = Interlocked.Add(ref bytesDone, n);
                                meter.Notify(done);
                            }, token);
                            ok = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (RangeNotSupportedException) { throw; }
                        catch (Exception e)
                        {
                            chunkError = e;
                            Interlocked.Add(ref bytesDone, -attemptBytes);
                            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), token);
                        }
                    }
                    if (ok) break;
                }
                if (!ok) throw new IOException($"分片 {index + 1}/{chunkCount} 下载失败：{chunkError?.Message}", chunkError);
                lock (gate) { completed.Add(index); SaveStateLocked(); }
                meter.Flush(Interlocked.Read(ref bytesDone));
            }
        }

        async Task Run()
        {
            try { await Worker(); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception e)
            {
                if (Interlocked.CompareExchange(ref firstError, e, null) == null) linked.Cancel();
            }
        }

        var workers = Enumerable.Range(0, Math.Min(requestedConnections, chunkCount)).Select(_ => Task.Run(Run)).ToArray();
        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();
        if (firstError != null) throw firstError;
    }

    private static async Task DownloadRangeAsync(string url, SafeFileHandle handle, long offset, int length,
        Action<int> onBytes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.OK) throw new RangeNotSupportedException();
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentRange?.From is long from && from != offset)
            throw new IOException("服务器返回了错误的分片范围");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1 << 20];
        long written = 0;
        while (written < length)
        {
            int want = (int)Math.Min(buffer.Length, length - written);
            int n = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
            if (n <= 0) break;
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, n), offset + written, ct);
            written += n;
            onBytes(n);
        }
        if (written != length) throw new IOException($"分片不完整（{written}/{length}）");
    }

    private static async Task DownloadOneAsync(string url, string part, long expected,
        Action<long, long, double>? onProgress, CancellationToken ct)
    {
        long start = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expected > 0 && start > expected) { File.Delete(part); start = 0; }
        if (expected > 0 && start == expected) return;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (start > 0) req.Headers.Range = new RangeHeaderValue(start, null);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && expected > 0 && start == expected) return;
        if (resp.StatusCode != HttpStatusCode.PartialContent && resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");

        bool append = resp.StatusCode == HttpStatusCode.PartialContent && start > 0;
        if (!append) start = 0;
        long total = resp.Content.Headers.ContentRange?.Length ?? (expected > 0 ? expected : (resp.Content.Headers.ContentLength ?? 0) + start);
        await using var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1 << 20];
        long done = start;
        long lastDone = start;
        long lastMs = 0;
        var watch = Stopwatch.StartNew();
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            var ms = watch.ElapsedMilliseconds;
            if (ms - lastMs >= 300)
            {
                var speed = Math.Max(0, (done - lastDone) / Math.Max(0.001, (ms - lastMs) / 1000.0));
                onProgress?.Invoke(done, total, speed);
                lastDone = done;
                lastMs = ms;
            }
        }
        await file.FlushAsync(ct);
        onProgress?.Invoke(done, total, 0);
    }

    private static bool TryFinish(string part, string state, string finalPath, FileSpec spec)
    {
        try { VerifyFile(part, spec); }
        catch (Exception)
        {
            TryDelete(part);
            TryDelete(state);
            return false;
        }
        File.Move(part, finalPath, true);
        TryDelete(state);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    private sealed class DownloadMeter
    {
        private readonly long _total;
        private readonly Action<long, long, double>? _report;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _lastBytes;
        private long _lastMs;

        public DownloadMeter(long start, long total, Action<long, long, double>? report)
        {
            _lastBytes = start;
            _total = total;
            _report = report;
            _report?.Invoke(start, total, 0);
        }

        public void Notify(long done)
        {
            lock (_gate)
            {
                var ms = _watch.ElapsedMilliseconds;
                if (ms - _lastMs < 250) return;
                var speed = Math.Max(0, (done - _lastBytes) / Math.Max(0.001, (ms - _lastMs) / 1000.0));
                _lastBytes = done;
                _lastMs = ms;
                _report?.Invoke(done, _total, speed);
            }
        }

        public void Flush(long done)
        {
            lock (_gate) _report?.Invoke(done, _total, 0);
        }
    }

    private static void VerifyFile(string path, FileSpec spec)
    {
        var info = new FileInfo(path);
        if (spec.Size > 0 && info.Length != spec.Size)
            throw new IOException($"{spec.Name}大小不完整（{FormatSize(info.Length)} / {FormatSize(spec.Size)}）");
        if (!string.IsNullOrWhiteSpace(spec.Sha256))
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            if (!hash.Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{spec.Name}校验失败，请重试。");
        }
    }

    private static bool IsComplete(string path, long size) => File.Exists(path) && (size <= 0 || new FileInfo(path).Length == size);

    private static void EnsureWritable()
    {
        try
        {
            var dir = Path.Combine(AppPaths.Root, "runtime");
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("程序目录不可写。请把程序移到用户目录后再安装依赖。");
        }
    }

    private static void EnsureDisk(long need)
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.Root);
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < need)
                throw new InvalidOperationException($"磁盘空间不足：至少需要 {FormatSize(need)}，当前可用 {FormatSize(free)}。");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception) { /* 网络驱动器无法读取容量时不阻塞 */ }
    }

    private static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动：" + exe);
        var tail = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Append(tail, e.Data);
            try { onLine?.Invoke(e.Data); } catch (Exception) { }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Append(tail, e.Data);
            try { onLine?.Invoke(e.Data); } catch (Exception) { }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception) { }
            throw;
        }
        string text;
        lock (tail) text = tail.ToString();
        return new RunResult(process.ExitCode, text);
    }

    private static void Append(StringBuilder sb, string line)
    {
        lock (sb)
        {
            sb.AppendLine(line);
            if (sb.Length > 12000) sb.Remove(0, sb.Length - 12000);
        }
    }

    private static string ResultTail(RunResult r)
    {
        var t = r.Tail.Trim();
        if (t.Length == 0) return "退出码 " + r.Code;
        return t.Length > 900 ? t[^900..] : t;
    }

    private static void Report(IProgress<DepProgress> p, string stage, double percent, string detail) =>
        p.Report(new DepProgress(stage, Math.Clamp(percent, 0, 100), detail));

    private static void MapProgress(IProgress<DepProgress> p, string stage, double from, double to, long done, long total, double bytesPerSecond = 0)
    {
        var ratio = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
        var detail = $"{FormatSize(done)} / {(total > 0 ? FormatSize(total) : "未知")}";
        if (bytesPerSecond > 1024)
        {
            detail += $" · {FormatSize((long)bytesPerSecond)}/s";
            if (total > done) detail += $" · 剩余 {FormatEta((total - done) / bytesPerSecond)}";
        }
        Report(p, stage, from + (to - from) * ratio, detail);
    }

    private static string FormatEta(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    private static string SitePackages => Path.Combine(Path.GetDirectoryName(AppPaths.Python)!, "Lib", "site-packages");
    private static string Downloads => Path.Combine(AppPaths.Root, "runtime", "downloads");

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromHours(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Beadwatch/2.4");
        return c;
    }

    private sealed record RunResult(int Code, string Tail);
}

public sealed class DepManifest
{
    public int Schema { get; set; }
    public string Version { get; set; } = "";
    public string Site { get; set; } = "";
    public PythonSpec Python { get; set; } = new();
    public FileSpec GetPip { get; set; } = new();
    public List<string> Indexes { get; set; } = new();
    public TorchSpec Torch { get; set; } = new();
    public List<string> TorchDeps { get; set; } = new();
    public List<string> FullPackages { get; set; } = new();
    public List<string> CutoutPackages { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public List<ModelSpec> Models { get; set; } = new();
    public ModelSpec Cutout { get; set; } = new();
}

public class FileSpec
{
    public string Name { get; set; } = "依赖文件";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public List<string> Urls { get; set; } = new();
}

public sealed class PythonSpec : FileSpec
{
    public string Version { get; set; } = "3.11.9";
}

public sealed class TorchSpec
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string TorchvisionVersion { get; set; } = "";
    public FileSpec Torch { get; set; } = new();
    public FileSpec Torchvision { get; set; } = new();
    /// <summary>变体适用的最低 NVIDIA 计算能力（例如 RTX 50 系为 12.0）。</summary>
    public double MinCompute { get; set; }
    public List<TorchSpec> Variants { get; set; } = new();
}

public sealed class ModelSpec : FileSpec
{
    public string Path { get; set; } = "";
    public string? ConfigPath { get; set; }
    public string? Config { get; set; }
}
