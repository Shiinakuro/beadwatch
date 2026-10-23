using System.Diagnostics;
using System.Globalization;

namespace Doudou.Core.Services;

public sealed record GpuInfo(string Name, int VramMb, double ComputeCap);

/// <summary>读取 NVIDIA 显卡与显存，用于判断本地混元模型能否运行。</summary>
public static class GpuProbe
{
    private static readonly object Gate = new();
    private static GpuInfo? _cached;
    private static bool _done;

    /// <summary>本地混元 2mv 的显存门槛（MB）。低于此值建议在线 API 或轮廓雕刻。</summary>
    public const int LocalHunyuanMinVramMb = 6000;

    public static GpuInfo? Detect(bool refresh = false)
    {
        lock (Gate)
        {
            if (_done && !refresh) return _cached;
            _cached = DetectCore();
            _done = true;
            return _cached;
        }
    }

    public static bool CanRunLocalHunyuan => Detect() is { } g && g.VramMb >= LocalHunyuanMinVramMb;

    public static string LocalChoiceText
    {
        get
        {
            var g = Detect();
            if (g == null) return "未检测到 NVIDIA 显卡：本地混元不可用，可用在线 3.1 或轮廓雕刻。";
            if (g.VramMb < LocalHunyuanMinVramMb)
                return $"{g.Name} · {g.VramMb} MB 显存：低于本地混元 2mv 的约 6 GB 门槛，建议在线 3.1 或轮廓雕刻。";
            return $"{g.Name} · {g.VramMb} MB 显存 · 计算能力 {g.ComputeCap:0.0}：本地最大可运行模型为混元 2mv（多视图）。";
        }
    }

    private static GpuInfo? DetectCore()
    {
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
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                psi.ArgumentList.Add("--query-gpu=name,memory.total,compute_cap");
                psi.ArgumentList.Add("--format=csv,noheader,nounits");
                using var p = Process.Start(psi);
                if (p == null) continue;
                var output = p.StandardOutput.ReadToEnd();
                var errors = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } }
                if (p.ExitCode != 0 || output.Trim().Length == 0) continue;

                GpuInfo? best = null;
                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = raw.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram)) continue;
                    double cap = 0;
                    if (parts.Length >= 3) double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out cap);
                    var info = new GpuInfo(parts[0], vram, cap);
                    if (best == null || info.VramMb > best.VramMb) best = info;
                }
                if (best != null) return best;
            }
            catch (Exception) { }
        }
        return null;
    }
}
