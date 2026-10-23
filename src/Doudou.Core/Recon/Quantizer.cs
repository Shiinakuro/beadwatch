using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Recon;

public sealed class QuantizeOptions
{
    public string BrandId { get; set; } = "mard221";
    /// <summary>最多使用的颜色数（真人图纸常见 10~25 色）。0 = 不限。</summary>
    public int MaxColors { get; set; } = 20;
    /// <summary>去掉孤立杂色豆：与周围多数色 ΔE 小于该值的单颗异色豆会被并掉。</summary>
    public double DespeckleDeltaE { get; set; } = 12;
    /// <summary>暗色细节保护（眼睛、嘴、描边）：比周围暗这么多 L* 的孤立豆不并。</summary>
    public double ProtectDarkL { get; set; } = 25;
    /// <summary>只允许用这些色号（可空 = 整套色卡）。</summary>
    public HashSet<string>? AllowedCodes { get; set; }
}

/// <summary>连续色 → 品牌色号，并控制颜色数、清理杂色。</summary>
public static class Quantizer
{
    public static VoxelModel Build(Occupancy occ, Lab?[] colors, PaletteLibrary lib, QuantizeOptions opt, string name)
    {
        var brand = lib.Get(opt.BrandId);
        var allowed = opt.AllowedCodes == null ? brand.Colors : brand.Colors.Where(c => opt.AllowedCodes.Contains(c.Code)).ToList();
        if (allowed.Count == 0) allowed = brand.Colors;

        int n = occ.Cells.Length;
        var assign = new BrandColor?[n];
        var cache = new Dictionary<(int, int, int), BrandColor>();
        for (int i = 0; i < n; i++)
        {
            if (!occ.Cells[i]) continue;
            var lab = colors[i] ?? new Lab(80, 0, 0);
            var key = ((int)Math.Round(lab.L), (int)Math.Round(lab.A), (int)Math.Round(lab.B));
            if (!cache.TryGetValue(key, out var c))
            {
                c = brand.Nearest(lab, allowed).Color;
                cache[key] = c;
            }
            assign[i] = c;
        }

        // 只统计「表面豆」的用色来决定保留哪些颜色——内部豆看不见，不该影响取舍
        var surface = SurfaceMask(occ);
        if (opt.MaxColors > 0) ReduceColors(occ, colors, assign, surface, opt.MaxColors);
        if (opt.DespeckleDeltaE > 0) Despeckle(occ, assign, surface, opt);

        var m = new VoxelModel(occ.SX, occ.SY, occ.SZ) { BrandId = brand.Id, Name = name };
        var idx = new Dictionary<string, ushort>();
        for (int i = 0; i < n; i++)
        {
            var c = assign[i];
            if (c == null) continue;
            if (!idx.TryGetValue(c.Code, out var k)) { k = m.EnsureColor(c.Code, c.Rgb); idx[c.Code] = k; }
            m.Colors[i] = k;
        }
        m.Touch();
        return m;
    }

    public static bool[] SurfaceMask(Occupancy occ)
    {
        var s = new bool[occ.Cells.Length];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    if (!occ[x, y, z]) continue;
                    foreach (var d in Dir6.All)
                        if (!occ[x + d.X, y + d.Y, z + d.Z]) { s[occ.Index(x, y, z)] = true; break; }
                }
        return s;
    }

    private static void ReduceColors(Occupancy occ, Lab?[] lab, BrandColor?[] assign, bool[] surface, int maxColors)
    {
        while (true)
        {
            var usage = new Dictionary<BrandColor, int>();
            for (int i = 0; i < assign.Length; i++)
                if (assign[i] != null) usage[assign[i]!] = usage.GetValueOrDefault(assign[i]!) + (surface[i] ? 1 : 0);
            if (usage.Count <= maxColors) return;
            // 去掉「用得最少 × 与其他色最接近」的颜色：优先合并既少又不独特的
            BrandColor? victim = null; double best = double.MaxValue;
            foreach (var (c, cnt) in usage)
            {
                double nearest = usage.Keys.Where(o => o != c).Min(o => ColorMath.DeltaE2000(o.Lab, c.Lab));
                double score = (cnt + 1) * Math.Min(nearest, 30);
                if (score < best) { best = score; victim = c; }
            }
            var keep = usage.Keys.Where(c => c != victim).ToList();
            for (int i = 0; i < assign.Length; i++)
            {
                if (assign[i] != victim) continue;
                var l = lab[i] ?? victim!.Lab;
                assign[i] = keep.MinBy(c => ColorMath.DeltaE2000(c.Lab, l));
            }
        }
    }

    private static void Despeckle(Occupancy occ, BrandColor?[] assign, bool[] surface, QuantizeOptions opt)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (BrandColor?[])assign.Clone();
            for (int z = 0; z < occ.SZ; z++)
                for (int y = 0; y < occ.SY; y++)
                    for (int x = 0; x < occ.SX; x++)
                    {
                        int i = occ.Index(x, y, z);
                        if (!surface[i] || assign[i] == null) continue;
                        var me = assign[i]!;
                        var counts = new Dictionary<BrandColor, int>();
                        int same = 0, total = 0;
                        // 26 邻域中的表面豆
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0 && dz == 0) continue;
                                    if (!occ.In(x + dx, y + dy, z + dz)) continue;
                                    int j = occ.Index(x + dx, y + dy, z + dz);
                                    if (!surface[j] || assign[j] == null) continue;
                                    total++;
                                    if (assign[j] == me) same++;
                                    else counts[assign[j]!] = counts.GetValueOrDefault(assign[j]!) + 1;
                                }
                        if (total < 4 || same > 0 || counts.Count == 0) continue;
                        var (major, mc) = counts.MaxBy(kv => kv.Value);
                        if (mc < total * 0.5) continue;
                        // 暗色细节保护：孤立的深色点（眼睛高光以外的瞳孔、鼻孔等）保留
                        if (major.Lab.L - me.Lab.L >= opt.ProtectDarkL) continue;
                        if (ColorMath.DeltaE2000(major.Lab, me.Lab) <= opt.DespeckleDeltaE) next[i] = major;
                    }
            Array.Copy(next, assign, assign.Length);
        }
    }

    /// <summary>把任意来源的模型（如 .vox 原始色）重新映射到品牌色卡。</summary>
    public static void RemapToBrand(VoxelModel m, PaletteLibrary lib, string brandId)
    {
        var brand = lib.Get(brandId);
        var newIdx = new ushort[m.Palette.Count];
        var old = m.Palette.ToList();
        m.Palette.Clear(); m.Palette.Add(old[0]);
        for (int i = 1; i < old.Count; i++)
        {
            if (old[i].IsAny) { newIdx[i] = m.EnsureColor(old[i].Code, old[i].Rgb, true); continue; }
            var (c, _) = brand.Nearest(ColorMath.ToLab(old[i].Rgb));
            newIdx[i] = m.EnsureColor(c.Code, c.Rgb);
        }
        for (int i = 0; i < m.Colors.Length; i++) m.Colors[i] = newIdx[m.Colors[i]];
        m.BrandId = brand.Id;
        m.CompactPalette();
    }
}
