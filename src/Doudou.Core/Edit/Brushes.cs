using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Edit;

/// <summary>编辑动作（仿 MagicaVoxel）：贴附 / 擦除 / 上色；另有零件刷与选择。</summary>
public enum EditAction { Attach, Erase, Paint, PartPaint, Select, Pick }

/// <summary>笔刷形状：单格 / 整面 / 矩形 / 直线 / 同色填充 / 平面填充（零件拆分专用）。</summary>
public enum BrushMode { Voxel, Face, Box, Line, Fill, Plane }

public static class Brushes
{
    /// <summary>以中心 c 为中心、半径 r 的立方/球形区域。</summary>
    public static IEnumerable<Int3> Ball(Int3 c, int r, bool sphere)
    {
        for (int dz = -r; dz <= r; dz++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (sphere && dx * dx + dy * dy + dz * dz > r * r + r) continue;
                    yield return new Int3(c.X + dx, c.Y + dy, c.Z + dz);
                }
    }

    /// <summary>
    /// 「整面」：从命中面出发，在同一平面上找所有连通、同色、且该面朝外露出的豆。
    /// ignoreColor=true 时不要求同色。
    /// </summary>
    public static List<Int3> Face(VoxelModel m, Int3 start, Int3 normal, bool ignoreColor = false, int limit = 200000)
    {
        var res = new List<Int3>();
        if (!m.Solid(start)) return res;
        ushort col = m.Get(start);
        int axis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;
        var seen = new HashSet<Int3> { start };
        var q = new Queue<Int3>(); q.Enqueue(start);
        while (q.Count > 0 && res.Count < limit)
        {
            var p = q.Dequeue();
            res.Add(p);
            for (int d = 0; d < 6; d++)
            {
                if (Dir6.AxisOf(d) == axis) continue;
                var n = p + Dir6.All[d];
                if (seen.Contains(n) || !m.InBounds(n)) continue;
                var c = m.Get(n);
                if (c == 0 || (!ignoreColor && c != col)) continue;
                if (m.Solid(n + normal)) continue;   // 该面被挡住，不算露出的面
                seen.Add(n); q.Enqueue(n);
            }
        }
        return res;
    }

    /// <summary>
    /// 「平面填充」：在命中豆所在的、垂直于 axis 的那一层里，找所有连通的豆。
    /// samePart=true 时只扩散到与起点同零件号的豆（用于把一层切成零件）。
    /// </summary>
    public static List<Int3> Plane(VoxelModel m, Int3 start, int axis, bool samePart, bool sameColor = false, Func<int, bool>? allow = null)
    {
        var res = new List<Int3>();
        if (!m.Solid(start)) return res;
        int si = m.Index(start);
        ushort part = m.Parts[si], col = m.Colors[si];
        var seen = new HashSet<Int3> { start };
        var q = new Queue<Int3>(); q.Enqueue(start);
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            res.Add(p);
            for (int d = 0; d < 6; d++)
            {
                if (Dir6.AxisOf(d) == axis) continue;
                var n = p + Dir6.All[d];
                if (seen.Contains(n) || !m.InBounds(n)) continue;
                int i = m.Index(n);
                if (m.Colors[i] == 0) continue;
                if (samePart && m.Parts[i] != part) continue;
                if (sameColor && m.Colors[i] != col) continue;
                if (allow != null && !allow(i)) continue;
                seen.Add(n); q.Enqueue(n);
            }
        }
        return res;
    }

    /// <summary>三维同色连通填充（上色用）。</summary>
    public static List<Int3> Fill(VoxelModel m, Int3 start, int limit = 2000000)
    {
        var res = new List<Int3>();
        if (!m.Solid(start)) return res;
        ushort col = m.Get(start);
        var seen = new HashSet<Int3> { start };
        var q = new Queue<Int3>(); q.Enqueue(start);
        while (q.Count > 0 && res.Count < limit)
        {
            var p = q.Dequeue();
            res.Add(p);
            foreach (var d in Dir6.All)
            {
                var n = p + d;
                if (seen.Contains(n) || m.Get(n) != col) continue;
                seen.Add(n); q.Enqueue(n);
            }
        }
        return res;
    }

    /// <summary>两点之间的矩形区域（含），两点任一轴相同时就是平面矩形。</summary>
    public static IEnumerable<Int3> Box(Int3 a, Int3 b)
    {
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
            for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
                for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
                    yield return new Int3(x, y, z);
    }

    /// <summary>三维 Bresenham 直线。</summary>
    public static List<Int3> Line(Int3 a, Int3 b)
    {
        var res = new List<Int3>();
        int n = Math.Max(Math.Abs(b.X - a.X), Math.Max(Math.Abs(b.Y - a.Y), Math.Abs(b.Z - a.Z)));
        if (n == 0) { res.Add(a); return res; }
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            var p = new Int3((int)MathF.Round(a.X + (b.X - a.X) * t), (int)MathF.Round(a.Y + (b.Y - a.Y) * t), (int)MathF.Round(a.Z + (b.Z - a.Z) * t));
            if (res.Count == 0 || res[^1] != p) res.Add(p);
        }
        return res;
    }

    /// <summary>按镜像开关展开一组坐标（关于模型中心对称）。</summary>
    public static IEnumerable<Int3> Mirrored(VoxelModel m, IEnumerable<Int3> cells, bool mx, bool my, bool mz)
    {
        var set = new HashSet<Int3>();
        foreach (var c in cells)
        {
            set.Add(c);
            if (mx) set.Add(c with { X = m.SX - 1 - c.X });
            if (my) set.Add(c with { Y = m.SY - 1 - c.Y });
            if (mz) set.Add(c with { Z = m.SZ - 1 - c.Z });
            if (mx && my) set.Add(new Int3(m.SX - 1 - c.X, m.SY - 1 - c.Y, c.Z));
            if (mx && mz) set.Add(new Int3(m.SX - 1 - c.X, c.Y, m.SZ - 1 - c.Z));
            if (my && mz) set.Add(new Int3(c.X, m.SY - 1 - c.Y, m.SZ - 1 - c.Z));
            if (mx && my && mz) set.Add(new Int3(m.SX - 1 - c.X, m.SY - 1 - c.Y, m.SZ - 1 - c.Z));
        }
        return set;
    }
}
