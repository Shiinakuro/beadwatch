using Doudou.Core.Color;
using Doudou.Core.Geometry;

namespace Doudou.Core.Model;

/// <summary>模型里实际使用的一种豆子颜色。</summary>
public sealed class BeadColor
{
    public string Code { get; set; } = "";
    public Rgb Rgb { get; set; }
    /// <summary>任意色：藏在内部、看不见的填充豆，可用任何颜色（真人图纸里的「任意色」）。</summary>
    public bool IsAny { get; set; }
    public string Note { get; set; } = "";

    public BeadColor Clone() => new() { Code = Code, Rgb = Rgb, IsAny = IsAny, Note = Note };
    public override string ToString() => IsAny ? "任意色" : Code;
}

/// <summary>零件（一块可独立熨烫的平板）。</summary>
public sealed class PartInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>平板法线轴：0=X,1=Y,2=Z；-1 = 未知/不是平板。由分析结果回填。</summary>
    public int PlaneAxis { get; set; } = -1;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    /// <summary>熨烫后需要翻面（图纸标注「此零件需翻转」）。</summary>
    public bool Flip { get; set; }
    public string Group { get; set; } = "";
    public string Note { get; set; } = "";
    public Rgb Tint { get; set; }

    public PartInfo Clone() => (PartInfo)MemberwiseClone();
}

/// <summary>
/// 体素真源。稠密存储：Colors[i] = 模型调色板下标（0 = 空），Parts[i] = 零件号（0 = 未分配）。
/// 坐标约定：X 向右，Y 向上，Z 指向正面观察者。一个体素 = 一颗豆。
/// </summary>
public sealed class VoxelModel
{
    public int SX { get; private set; }
    public int SY { get; private set; }
    public int SZ { get; private set; }
    public ushort[] Colors { get; private set; }
    public ushort[] Parts { get; private set; }

    /// <summary>模型调色板。下标 0 保留为「空」。</summary>
    public List<BeadColor> Palette { get; } = new() { new BeadColor { Code = "(空)" } };
    public List<PartInfo> PartList { get; } = new();

    /// <summary>所依据的品牌色卡（决定色号体系）。</summary>
    public string BrandId { get; set; } = "mard221";
    public string Name { get; set; } = "未命名";

    /// <summary>每次改动自增，供渲染缓存判断是否需要重建。</summary>
    public int Version { get; private set; }
    public void Touch() => Version++;

    public VoxelModel(int sx, int sy, int sz)
    {
        if (sx <= 0 || sy <= 0 || sz <= 0) throw new ArgumentException("尺寸必须为正");
        if ((long)sx * sy * sz > 512L * 512 * 512) throw new ArgumentException("模型过大");
        SX = sx; SY = sy; SZ = sz;
        Colors = new ushort[sx * sy * sz];
        Parts = new ushort[sx * sy * sz];
    }

    public int Count => Colors.Length;

    public bool InBounds(int x, int y, int z) => (uint)x < (uint)SX && (uint)y < (uint)SY && (uint)z < (uint)SZ;
    public bool InBounds(Int3 p) => InBounds(p.X, p.Y, p.Z);

    public int Index(int x, int y, int z) => (z * SY + y) * SX + x;
    public int Index(Int3 p) => Index(p.X, p.Y, p.Z);

    public Int3 Pos(int i)
    {
        int x = i % SX; int t = i / SX;
        return new Int3(x, t % SY, t / SY);
    }

    public ushort Get(int x, int y, int z) => InBounds(x, y, z) ? Colors[Index(x, y, z)] : (ushort)0;
    public ushort Get(Int3 p) => Get(p.X, p.Y, p.Z);
    public bool Solid(int x, int y, int z) => Get(x, y, z) != 0;
    public bool Solid(Int3 p) => Get(p) != 0;

    public void Set(Int3 p, ushort color)
    {
        if (!InBounds(p)) return;
        int i = Index(p);
        Colors[i] = color;
        if (color == 0) Parts[i] = 0;
    }

    public int Size(int axis) => axis switch { 0 => SX, 1 => SY, _ => SZ };

    public int CountSolid()
    {
        int n = 0;
        foreach (var c in Colors) if (c != 0) n++;
        return n;
    }

    public IEnumerable<int> SolidIndices()
    {
        for (int i = 0; i < Colors.Length; i++) if (Colors[i] != 0) yield return i;
    }

    // ───────────── 调色板 ─────────────

    /// <summary>找到或追加一种颜色，返回下标。</summary>
    public ushort EnsureColor(string code, Rgb rgb, bool isAny = false)
    {
        for (int i = 1; i < Palette.Count; i++)
        {
            var c = Palette[i];
            if (c.IsAny == isAny && c.Code == code && (isAny || c.Rgb == rgb)) return (ushort)i;
        }
        if (Palette.Count >= ushort.MaxValue) throw new InvalidOperationException("颜色过多");
        Palette.Add(new BeadColor { Code = code, Rgb = rgb, IsAny = isAny });
        return (ushort)(Palette.Count - 1);
    }

    public ushort AnyColorIndex(Rgb display) => EnsureColor("任意色", display, isAny: true);

    public Rgb RgbOf(ushort c) => c < Palette.Count ? Palette[c].Rgb : default;

    /// <summary>删掉未使用的颜色并重排下标。</summary>
    public void CompactPalette()
    {
        var used = new bool[Palette.Count];
        foreach (var c in Colors) used[c] = true;
        var remap = new ushort[Palette.Count];
        var keep = new List<BeadColor> { Palette[0] };
        for (int i = 1; i < Palette.Count; i++)
            if (used[i]) { remap[i] = (ushort)keep.Count; keep.Add(Palette[i]); }
        for (int i = 0; i < Colors.Length; i++) Colors[i] = remap[Colors[i]];
        Palette.Clear();
        Palette.AddRange(keep);
        Touch();
    }

    // ───────────── 零件 ─────────────

    public PartInfo? FindPart(int id) => PartList.FirstOrDefault(p => p.Id == id);

    public PartInfo NewPart(string? name = null)
    {
        int id = PartList.Count == 0 ? 1 : PartList.Max(p => p.Id) + 1;
        if (id > ushort.MaxValue) throw new InvalidOperationException("零件过多");
        var p = new PartInfo { Id = id, Name = name ?? $"P{id:000}", Tint = PartTint(id) };
        PartList.Add(p);
        return p;
    }

    public static Rgb PartTint(int id)
    {
        // 黄金角取色，相邻编号颜色差别大
        double h = (id * 137.508) % 360 / 360.0;
        return Hsv(h, 0.55, 0.95);
    }

    private static Rgb Hsv(double h, double s, double v)
    {
        double r = 0, g = 0, b = 0;
        int i = (int)(h * 6); double f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return new Rgb(Rgb.Clamp(r * 255), Rgb.Clamp(g * 255), Rgb.Clamp(b * 255));
    }

    /// <summary>移除不再有任何豆的零件记录。</summary>
    public void PruneEmptyParts()
    {
        var used = new HashSet<int>();
        for (int i = 0; i < Parts.Length; i++) if (Colors[i] != 0 && Parts[i] != 0) used.Add(Parts[i]);
        PartList.RemoveAll(p => !used.Contains(p.Id));
    }

    // ───────────── 整体变换 ─────────────

    public VoxelModel Clone()
    {
        var m = new VoxelModel(SX, SY, SZ) { BrandId = BrandId, Name = Name };
        Array.Copy(Colors, m.Colors, Colors.Length);
        Array.Copy(Parts, m.Parts, Parts.Length);
        m.Palette.Clear();
        m.Palette.AddRange(Palette.Select(c => c.Clone()));
        m.PartList.AddRange(PartList.Select(p => p.Clone()));
        return m;
    }

    /// <summary>包围盒（无豆时返回 null）。</summary>
    public (Int3 Min, Int3 Max)? Bounds()
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        for (int i = 0; i < Colors.Length; i++)
        {
            if (Colors[i] == 0) continue;
            var p = Pos(i);
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
        }
        return x1 < 0 ? null : (new Int3(x0, y0, z0), new Int3(x1, y1, z1));
    }

    /// <summary>改变画布尺寸，内容按 offset 平移（越界部分丢弃）。</summary>
    public VoxelModel Resized(int sx, int sy, int sz, Int3 offset)
    {
        var m = new VoxelModel(sx, sy, sz) { BrandId = BrandId, Name = Name };
        m.Palette.Clear(); m.Palette.AddRange(Palette.Select(c => c.Clone()));
        m.PartList.AddRange(PartList.Select(p => p.Clone()));
        for (int i = 0; i < Colors.Length; i++)
        {
            if (Colors[i] == 0) continue;
            var q = Pos(i) + offset;
            if (!m.InBounds(q)) continue;
            int j = m.Index(q);
            m.Colors[j] = Colors[i]; m.Parts[j] = Parts[i];
        }
        return m;
    }

    /// <summary>裁剪到包围盒，四周留 pad 格空白。</summary>
    public VoxelModel Cropped(int pad = 0)
    {
        var b = Bounds();
        if (b == null) return Clone();
        var (mn, mx) = b.Value;
        return Resized(mx.X - mn.X + 1 + 2 * pad, mx.Y - mn.Y + 1 + 2 * pad, mx.Z - mn.Z + 1 + 2 * pad,
            new Int3(pad - mn.X, pad - mn.Y, pad - mn.Z));
    }

    /// <summary>绕 Y 轴旋转 90°×k（俯视逆时针）。</summary>
    public VoxelModel RotatedY(int k)
    {
        k = ((k % 4) + 4) % 4;
        if (k == 0) return Clone();
        int nx = k % 2 == 0 ? SX : SZ, nz = k % 2 == 0 ? SZ : SX;
        var m = new VoxelModel(nx, SY, nz) { BrandId = BrandId, Name = Name };
        m.Palette.Clear(); m.Palette.AddRange(Palette.Select(c => c.Clone()));
        m.PartList.AddRange(PartList.Select(p => p.Clone()));
        for (int i = 0; i < Colors.Length; i++)
        {
            if (Colors[i] == 0) continue;
            var p = Pos(i);
            Int3 q = k switch
            {
                1 => new Int3(p.Z, p.Y, SX - 1 - p.X),
                2 => new Int3(SX - 1 - p.X, p.Y, SZ - 1 - p.Z),
                _ => new Int3(SZ - 1 - p.Z, p.Y, p.X),
            };
            int j = m.Index(q);
            m.Colors[j] = Colors[i]; m.Parts[j] = Parts[i];
        }
        foreach (var part in m.PartList)
            if (part.PlaneAxis == 0 || part.PlaneAxis == 2) part.PlaneAxis = k % 2 == 1 ? 2 - part.PlaneAxis : part.PlaneAxis;
        return m;
    }

    /// <summary>沿某轴镜像。</summary>
    public void Mirror(int axis)
    {
        var c2 = new ushort[Colors.Length]; var p2 = new ushort[Parts.Length];
        for (int i = 0; i < Colors.Length; i++)
        {
            if (Colors[i] == 0) continue;
            var p = Pos(i);
            var q = p.With(axis, Size(axis) - 1 - p[axis]);
            int j = Index(q);
            c2[j] = Colors[i]; p2[j] = Parts[i];
        }
        Colors = c2; Parts = p2;
        Touch();
    }

    /// <summary>用另一个同尺寸模型的数据整体替换（撤销快照用）。</summary>
    public void CopyFrom(VoxelModel src)
    {
        SX = src.SX; SY = src.SY; SZ = src.SZ;
        Colors = (ushort[])src.Colors.Clone();
        Parts = (ushort[])src.Parts.Clone();
        Palette.Clear(); Palette.AddRange(src.Palette.Select(c => c.Clone()));
        PartList.Clear(); PartList.AddRange(src.PartList.Select(p => p.Clone()));
        BrandId = src.BrandId; Name = src.Name;
        Touch();
    }
}
