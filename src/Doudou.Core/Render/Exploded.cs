using System.Numerics;
using Doudou.Core.Model;
using Doudou.Core.Parts;

namespace Doudou.Core.Render;

/// <summary>爆炸图布局：每个零件沿自身板面法线方向、按离中心的距离成比例拉开。</summary>
public static class Exploded
{
    public sealed class Layout
    {
        public List<RenderBead> Beads = new();
        public Dictionary<int, Vector3> Offsets = new();
        /// <summary>零件标签锚点（已含偏移，模型空间）。</summary>
        public Dictionary<int, Vector3> Anchors = new();
    }

    /// <param name="spread">1 = 不拉开；2 = 每层之间多出一层的空隙。</param>
    /// <param name="includePart">只画部分零件（装配步骤图用）；null = 全部。</param>
    /// <param name="highlight">高亮的零件（当前步骤新加入的件）。</param>
    public static Layout Build(VoxelModel m, ModelPartsReport rep, float spread = 2.2f, Func<int, bool>? includePart = null,
        int highlight = -1, bool tint = false, float minGap = 2f)
    {
        var lay = new Layout();
        var center = new Vector3(m.SX, m.SY, m.SZ) / 2;
        foreach (var p in rep.Parts)
        {
            if (includePart != null && !includePart(p.Id)) continue;
            var c = Centroid(m, p);
            Vector3 off;
            if (p.PlaneAxis >= 0)
            {
                int a = p.PlaneAxis;
                float delta = a == 0 ? c.X - center.X : a == 1 ? c.Y - center.Y : c.Z - center.Z;
                float sign = delta >= 0 ? 1 : -1;
                float mag = MathF.Abs(delta) * (spread - 1) + (spread > 1 ? minGap : 0);
                off = a == 0 ? new Vector3(sign * mag, 0, 0) : a == 1 ? new Vector3(0, sign * mag, 0) : new Vector3(0, 0, sign * mag);
            }
            else off = (c - center) * (spread - 1);
            lay.Offsets[p.Id] = off;
            lay.Anchors[p.Id] = c + off;
            var info = m.FindPart(p.Id);
            foreach (var i in p.Cells)
            {
                var col = tint && info != null ? info.Tint : m.RgbOf(m.Colors[i]);
                lay.Beads.Add(new RenderBead { Cell = m.Pos(i), Offset = off, Color = col, Alpha = 255, Group = p.Id, Highlight = p.Id == highlight });
            }
        }
        return lay;
    }

    public static Vector3 Centroid(VoxelModel m, PartReport p)
    {
        var s = Vector3.Zero;
        foreach (var i in p.Cells) { var q = m.Pos(i); s += new Vector3(q.X + 0.5f, q.Y + 0.5f, q.Z + 0.5f); }
        return p.Cells.Count == 0 ? s : s / p.Cells.Count;
    }
}
