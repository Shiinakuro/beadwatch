using System.Text.Json;

namespace Doudou.Core.Color;

/// <summary>一个品牌色卡中的一个颜色。</summary>
public sealed record BrandColor(string Code, Rgb Rgb, string Group)
{
    public Lab Lab { get; } = ColorMath.ToLab(Rgb);
}

/// <summary>品牌色卡（MARD、COCO、漫漫……）。</summary>
public sealed class BrandPalette
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<BrandColor> Colors { get; init; } = new();

    private Dictionary<string, BrandColor>? _byCode;

    public BrandColor? Find(string code)
    {
        _byCode ??= Colors.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byCode.TryGetValue(code, out var c) ? c : null;
    }

    /// <summary>CIEDE2000 最近色。allowed 不为空时只在其中挑。</summary>
    public (BrandColor Color, double DeltaE) Nearest(Lab lab, IReadOnlyCollection<BrandColor>? allowed = null)
    {
        BrandColor best = Colors[0];
        double bestD = double.MaxValue;
        foreach (var c in (IEnumerable<BrandColor>?)allowed ?? Colors)
        {
            // 先用欧氏距离粗筛，节省大量 CIEDE2000 计算
            double dl = c.Lab.L - lab.L, da = c.Lab.A - lab.A, db = c.Lab.B - lab.B;
            double rough = dl * dl + da * da + db * db;
            if (rough > bestD * bestD * 9 + 400) continue;
            double d = ColorMath.DeltaE2000(lab, c.Lab);
            if (d < bestD) { bestD = d; best = c; }
        }
        return (best, bestD);
    }

    public static BrandPalette Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var list = new List<BrandColor>();
        foreach (var c in root.GetProperty("colors").EnumerateArray())
        {
            var rgb = c.GetProperty("rgb");
            list.Add(new BrandColor(
                c.GetProperty("code").GetString() ?? "?",
                new Rgb((byte)rgb[0].GetInt32(), (byte)rgb[1].GetInt32(), (byte)rgb[2].GetInt32()),
                c.TryGetProperty("group", out var g) ? g.GetString() ?? "" : ""));
        }
        return new BrandPalette
        {
            Id = root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(path),
            Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Colors = list,
        };
    }
}

/// <summary>全部色卡 + 跨品牌对照。</summary>
public sealed class PaletteLibrary
{
    public Dictionary<string, BrandPalette> Brands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图纸材料表默认展示的品牌列（与真人图纸一致：MARD / COCO / 卡卡 / 漫漫 / 盼盼 / 味小窝）。</summary>
    public static readonly (string Id, string Label)[] DrawingBrands =
    {
        ("mard221", "MARD"), ("coco291", "COCO"), ("artkalM221", "卡卡"),
        ("manman278", "漫漫"), ("panpan289", "盼盼"), ("mixiaowo290", "味小窝"),
    };

    /// <summary>人工校对过的 MARD221 → 其他品牌对照（来自色卡资料），优先于自动匹配。</summary>
    private readonly Dictionary<string, Dictionary<string, (string Code, double De)>> _crossMard = new(StringComparer.OrdinalIgnoreCase);

    public static PaletteLibrary LoadDirectory(string dir)
    {
        var lib = new PaletteLibrary();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("crossbrand_", StringComparison.OrdinalIgnoreCase)) { lib.LoadCross(f); continue; }
            try
            {
                var p = BrandPalette.Load(f);
                lib.Brands[p.Id] = p;
            }
            catch (Exception) { /* 跳过损坏的色卡文件 */ }
        }
        return lib;
    }

    private void LoadCross(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("rows", out var rows)) return;
        foreach (var row in rows.EnumerateObject())
        {
            var m = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
            if (row.Value.TryGetProperty("matches", out var matches))
                foreach (var t in matches.EnumerateObject())
                    m[t.Name] = (t.Value.GetProperty("code").GetString() ?? "", t.Value.GetProperty("delta_e").GetDouble());
            _crossMard[row.Name] = m;
        }
    }

    public BrandPalette Get(string id) =>
        Brands.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"未找到色卡 {id}");

    /// <summary>把某品牌色号换算到另一品牌。ΔE 超过 maxDe 返回 "-"（无对应）。</summary>
    public (string Code, double DeltaE) Convert(string fromBrand, string code, Rgb rgb, string toBrand, double maxDe = 8)
    {
        if (fromBrand.Equals(toBrand, StringComparison.OrdinalIgnoreCase)) return (code, 0);
        if (fromBrand.StartsWith("mard", StringComparison.OrdinalIgnoreCase)
            && _crossMard.TryGetValue(code, out var row) && row.TryGetValue(toBrand, out var hit))
            return hit.De <= maxDe ? hit : ("-", hit.De);
        if (!Brands.TryGetValue(toBrand, out var target)) return ("-", 99);
        var (c, de) = target.Nearest(ColorMath.ToLab(rgb));
        return de <= maxDe ? (c.Code, de) : ("-", de);
    }
}
