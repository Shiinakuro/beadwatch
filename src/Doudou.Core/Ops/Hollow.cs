using Doudou.Core.Color;
using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Ops;

public enum InteriorMode { Remove, AnyColor }

public sealed class HollowOptions
{
    /// <summary>外壳厚度（颗）。1 最省豆；2 更结实。</summary>
    public int Thickness { get; set; } = 1;
    public InteriorMode Interior { get; set; } = InteriorMode.Remove;
    /// <summary>加强筋：每隔 RibEvery 层（沿 RibAxis）在内部加一道「十字」任意色筋，0 = 不加。</summary>
    public int RibEvery { get; set; }
    public int RibAxis { get; set; } = 1;
    /// <summary>任意色在界面/图纸里的显示色。</summary>
    public Rgb AnyDisplay { get; set; } = new(60, 75, 70);
}

/// <summary>
/// 空心化：只保留离外表面 Thickness 颗以内的豆，内部挖空或标成「任意色」。
/// 真人图纸普遍这么做——实心模型豆量和重量都翻倍。
/// </summary>
public static class Hollow
{
    public static (int Removed, int AnyColored, int Ribs) Apply(VoxelModel m, EditHistory h, HollowOptions o)
    {
        var depth = DepthFromOutside(m);
        ushort any = m.AnyColorIndex(o.AnyDisplay);
        h.Begin("空心化");
        int removed = 0, anyN = 0, ribs = 0;
        var interior = new List<int>();
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Colors[i] == 0 || depth[i] <= o.Thickness) continue;
            interior.Add(i);
        }
        var ribSet = o.RibEvery > 0 ? RibCells(m, interior, o.RibAxis, o.RibEvery) : new HashSet<int>();
        foreach (var i in interior)
        {
            if (ribSet.Contains(i)) { h.SetColor(i, any); ribs++; continue; }
            if (o.Interior == InteriorMode.Remove) { h.SetColor(i, 0); removed++; }
            else { h.SetColor(i, any); anyN++; }
        }
        h.Commit();
        return (removed, anyN, ribs);
    }

    /// <summary>
    /// 每颗豆到外部空气的 26 邻域（棋盘）距离，表面豆 = 1。
    /// 必须用 26 邻域：用 6 邻域时斜坡处的单层壳只靠棱角相连，会漏光、散架。
    /// </summary>
    public static int[] DepthFromOutside(VoxelModel m)
    {
        var outside = OutsideAir(m);
        var depth = new int[m.Count];
        Array.Fill(depth, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Colors[i] == 0) continue;
            var p = m.Pos(i);
            bool surf = false;
            for (int dz = -1; dz <= 1 && !surf; dz++)
                for (int dy = -1; dy <= 1 && !surf; dy++)
                    for (int dx = -1; dx <= 1 && !surf; dx++)
                    {
                        var n = new Int3(p.X + dx, p.Y + dy, p.Z + dz);
                        if (!m.InBounds(n) || outside[m.Index(n)]) surf = true;
                    }
            if (surf) { depth[i] = 1; q.Enqueue(i); }
        }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            var p = m.Pos(i);
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var n = new Int3(p.X + dx, p.Y + dy, p.Z + dz);
                        if (!m.InBounds(n)) continue;
                        int j = m.Index(n);
                        if (m.Colors[j] == 0 || depth[j] <= depth[i] + 1) continue;
                        depth[j] = depth[i] + 1;
                        q.Enqueue(j);
                    }
        }
        return depth;
    }

    /// <summary>与网格外部连通的空格（真正的「外面」）。</summary>
    public static bool[] OutsideAir(VoxelModel m)
    {
        var o = new bool[m.Count];
        var q = new Queue<int>();
        void Seed(int x, int y, int z)
        {
            int i = m.Index(x, y, z);
            if (m.Colors[i] == 0 && !o[i]) { o[i] = true; q.Enqueue(i); }
        }
        for (int z = 0; z < m.SZ; z++) for (int y = 0; y < m.SY; y++) { Seed(0, y, z); Seed(m.SX - 1, y, z); }
        for (int z = 0; z < m.SZ; z++) for (int x = 0; x < m.SX; x++) { Seed(x, 0, z); Seed(x, m.SY - 1, z); }
        for (int y = 0; y < m.SY; y++) for (int x = 0; x < m.SX; x++) { Seed(x, y, 0); Seed(x, y, m.SZ - 1); }
        while (q.Count > 0)
        {
            var p = m.Pos(q.Dequeue());
            foreach (var d in Dir6.All)
            {
                var n = p + d;
                if (!m.InBounds(n)) continue;
                int j = m.Index(n);
                if (m.Colors[j] == 0 && !o[j]) { o[j] = true; q.Enqueue(j); }
            }
        }
        return o;
    }

    /// <summary>每隔 every 层，在该层内部区域的中线位置放一个十字筋。</summary>
    private static HashSet<int> RibCells(VoxelModel m, List<int> interior, int axis, int every)
    {
        var res = new HashSet<int>();
        foreach (var layer in interior.GroupBy(i => m.Pos(i)[axis]))
        {
            if (layer.Key % every != 0) continue;
            var cells = layer.ToList();
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            int amin = cells.Min(i => m.Pos(i)[a]), amax = cells.Max(i => m.Pos(i)[a]);
            int bmin = cells.Min(i => m.Pos(i)[b]), bmax = cells.Max(i => m.Pos(i)[b]);
            int amid = (amin + amax) / 2, bmid = (bmin + bmax) / 2;
            foreach (var i in cells)
            {
                var p = m.Pos(i);
                if (p[a] == amid || p[b] == bmid) res.Add(i);
            }
        }
        return res;
    }
}
