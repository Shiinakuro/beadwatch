using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Core.Ops;
using Doudou.Core.Parts;
using Doudou.Core.Recon;

namespace Doudou.Core.Check;

public enum LeakKind
{
    /// <summary>任意色（内芯）露在外面。</summary>
    CoreExposed,
    /// <summary>外壳破洞：能从外面看进内部空腔。</summary>
    ShellHole,
    /// <summary>豆子没有分到任何零件（图纸里会漏掉）。</summary>
    Unassigned,
    /// <summary>零件不是单层平板 / 不连通。</summary>
    BadPart,
    /// <summary>与参考图颜色不一致。</summary>
    ColorMismatch,
    /// <summary>悬空：与主体不相连的豆。</summary>
    Floating,
}

public sealed record LeakItem(LeakKind Kind, Int3 Cell, string Message, double Severity = 1);

public sealed class LeakReport
{
    public List<LeakItem> Items = new();
    public int TotalBeads;
    public int VisibleBeads;
    public double ReferenceAgreement = double.NaN;   // 0~1
    public int Count(LeakKind k) => Items.Count(i => i.Kind == k);
    public bool Clean => Items.All(i => i.Kind == LeakKind.ColorMismatch && i.Severity < 0.5);

    public string Summary()
    {
        var parts = new List<string>();
        void Add(LeakKind k, string label) { int c = Count(k); if (c > 0) parts.Add($"{label} {c}"); }
        Add(LeakKind.CoreExposed, "内芯外露");
        Add(LeakKind.ShellHole, "外壳破洞");
        Add(LeakKind.Unassigned, "未分零件");
        Add(LeakKind.BadPart, "问题零件");
        Add(LeakKind.Floating, "悬空豆");
        Add(LeakKind.ColorMismatch, "色差点");
        var s = parts.Count == 0 ? "未发现漏色" : string.Join("，", parts);
        if (!double.IsNaN(ReferenceAgreement)) s += $"；与参考图一致度 {ReferenceAgreement:P0}";
        return s;
    }
}

/// <summary>
/// 漏色检查：
///  1. 任意色（内部填充）是否有面朝外露出；
///  2. 空心外壳是否破洞（外部空气能进入原本封闭的内腔——内壁/内芯会被看到）；
///  3. 是否有豆没分到零件、零件是否可熨烫；
///  4. 悬空的豆；
///  5. 与参考视图逐格比色（外观一致性）。
/// </summary>
public static class LeakChecker
{
    public static LeakReport Check(VoxelModel m, IReadOnlyList<ViewImage>? refs = null, bool checkParts = true, double mismatchDeltaE = 18)
    {
        var rep = new LeakReport();
        var outside = Hollow.OutsideAir(m);
        for (int i = 0; i < m.Count; i++)
        {
            var c = m.Colors[i];
            if (c == 0) continue;
            rep.TotalBeads++;
            var p = m.Pos(i);
            bool visible = false;
            foreach (var d in Dir6.All)
            {
                var n = p + d;
                if (!m.InBounds(n) || outside[m.Index(n)]) { visible = true; break; }
            }
            if (!visible) continue;
            rep.VisibleBeads++;
            if (m.Palette[c].IsAny) rep.Items.Add(new LeakItem(LeakKind.CoreExposed, p, $"任意色内芯露在外面 {p}"));
        }

        // 外壳破洞：用「粗探针」从外面灌空气——只能穿过 3×3 以上开口的空气。
        // 外部空气里粗探针到不了、又成片（≥6 格）的部分，就是只能经由窄洞进入的空腔：从洞口能看到内部。
        foreach (var (cell, size) in NarrowPockets(m, outside))
            rep.Items.Add(new LeakItem(LeakKind.ShellHole, cell, $"外壳破洞 {cell}：通过窄口能看到内部空腔（{size} 格）"));

        if (checkParts)
        {
            var pr = PartAnalyzer.Analyze(m, writeBackAxis: false);
            if (pr.Parts.Count > 0)
            {
                foreach (var i in pr.Unassigned) rep.Items.Add(new LeakItem(LeakKind.Unassigned, m.Pos(i), $"{m.Pos(i)} 未分配零件"));
                foreach (var part in pr.Parts)
                    foreach (var issue in part.Issues.Where(x => x.Level == IssueLevel.Error))
                        rep.Items.Add(new LeakItem(LeakKind.BadPart, m.Pos(part.Cells[0]), $"{part.Name}：{issue.Message}"));
            }
        }

        // 悬空：最大连通块之外的豆
        var label = new int[m.Count];
        var sizes = new List<int> { 0 };
        var q2 = new Queue<int>();
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Colors[i] == 0 || label[i] != 0) continue;
            int id = sizes.Count, cnt = 0;
            label[i] = id; q2.Enqueue(i);
            while (q2.Count > 0)
            {
                var p = m.Pos(q2.Dequeue()); cnt++;
                foreach (var d in Dir6.All)
                {
                    var n = p + d;
                    if (!m.InBounds(n)) continue;
                    int j = m.Index(n);
                    if (m.Colors[j] != 0 && label[j] == 0) { label[j] = id; q2.Enqueue(j); }
                }
            }
            sizes.Add(cnt);
        }
        if (sizes.Count > 2)
        {
            int main = sizes.IndexOf(sizes.Max());
            for (int i = 0; i < m.Count; i++)
                if (m.Colors[i] != 0 && label[i] != main)
                    rep.Items.Add(new LeakItem(LeakKind.Floating, m.Pos(i), $"{m.Pos(i)} 与主体不相连"));
        }

        if (refs != null && refs.Count > 0) CompareWithReferences(m, refs, rep, mismatchDeltaE);
        return rep;
    }

    public static List<(Int3 Cell, int Size)> NarrowPockets(VoxelModel m, bool[] outside, int minSize = 6)
    {
        bool Clear(Int3 p)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var q = new Int3(p.X + dx, p.Y + dy, p.Z + dz);
                        if (m.InBounds(q) && m.Colors[m.Index(q)] != 0) return false;
                    }
            return true;
        }
        var fat = new bool[m.Count];
        var q2 = new Queue<int>();
        for (int i = 0; i < m.Count; i++)
        {
            if (!outside[i]) continue;
            var p = m.Pos(i);
            bool edge = p.X == 0 || p.Y == 0 || p.Z == 0 || p.X == m.SX - 1 || p.Y == m.SY - 1 || p.Z == m.SZ - 1;
            if (edge && Clear(p)) { fat[i] = true; q2.Enqueue(i); }
        }
        while (q2.Count > 0)
        {
            var p = m.Pos(q2.Dequeue());
            foreach (var d in Dir6.All)
            {
                var n = p + d;
                if (!m.InBounds(n)) continue;
                int j = m.Index(n);
                if (fat[j] || !outside[j] || !Clear(n)) continue;
                fat[j] = true; q2.Enqueue(j);
            }
        }
        // 粗探针扫过的区域 = 探针中心膨胀一圈
        var reached = new bool[m.Count];
        for (int i = 0; i < m.Count; i++)
        {
            if (!fat[i]) continue;
            var p = m.Pos(i);
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var n = new Int3(p.X + dx, p.Y + dy, p.Z + dz);
                        if (m.InBounds(n)) reached[m.Index(n)] = true;
                    }
        }
        var res = new List<(Int3, int)>();
        var seen = new bool[m.Count];
        for (int i = 0; i < m.Count; i++)
        {
            if (!outside[i] || reached[i] || seen[i]) continue;
            var comp = new List<int>();
            seen[i] = true; q2.Enqueue(i);
            while (q2.Count > 0)
            {
                int k = q2.Dequeue(); comp.Add(k);
                var p = m.Pos(k);
                foreach (var d in Dir6.All)
                {
                    var n = p + d;
                    if (!m.InBounds(n)) continue;
                    int j = m.Index(n);
                    if (seen[j] || !outside[j] || reached[j]) continue;
                    seen[j] = true; q2.Enqueue(j);
                }
            }
            // 只有内部够宽（存在 3×3×3 空块）的才算「空腔」；细窄的缝隙（臂下、下巴下）不算
            if (comp.Count < minSize || !comp.Any(k => Clear(m.Pos(k)))) continue;
            // 报告离粗探针区域最近的一格（洞口位置）
            int mouth = comp.FirstOrDefault(k => Dir6.All.Any(d => { var n = m.Pos(k) + d; return m.InBounds(n) && reached[m.Index(n)]; }), comp[0]);
            res.Add((m.Pos(mouth), comp.Count));
        }
        return res;
    }

    /// <summary>每张参考视图：模型在该方向第一眼看到的豆，与图片对应位置的颜色比较。</summary>
    public static void CompareWithReferences(VoxelModel m, IReadOnlyList<ViewImage> refs, LeakReport rep, double threshold)
    {
        var occ = new Occupancy(m.SX, m.SY, m.SZ);
        for (int i = 0; i < m.Count; i++) occ.Cells[i] = m.Colors[i] != 0;
        var proj = ColorProjector.Project(occ, refs);
        int compared = 0, agree = 0;
        var surface = Quantizer.SurfaceMask(occ);
        for (int i = 0; i < m.Count; i++)
        {
            if (!surface[i] || m.Colors[i] == 0 || !proj.Seen[i]) continue;
            var bead = m.Palette[m.Colors[i]];
            if (bead.IsAny) continue;
            compared++;
            double de = ColorMath.DeltaE2000(ColorMath.ToLab(bead.Rgb), proj.Colors[i]!.Value);
            if (de <= threshold) { agree++; continue; }
            rep.Items.Add(new LeakItem(LeakKind.ColorMismatch, m.Pos(i),
                $"{m.Pos(i)} {bead.Code} 与参考图色差 ΔE={de:F0}", Math.Min(1, de / 50)));
        }
        rep.ReferenceAgreement = compared == 0 ? double.NaN : (double)agree / compared;
    }
}
