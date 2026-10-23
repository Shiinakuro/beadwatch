using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Parts;

public enum IssueLevel { Info, Warning, Error }

public sealed record PartIssue(IssueLevel Level, string Message);

/// <summary>一个零件的几何与可制造性分析结果。</summary>
public sealed class PartReport
{
    public int Id;
    public string Name = "";
    public List<int> Cells = new();
    public Int3 Min, Max;
    /// <summary>平板法线轴（0/1/2），-1 = 不是单层平板。</summary>
    public int PlaneAxis = -1;
    public int PlaneCoord;
    public bool Planar;
    public int Islands;
    public Dictionary<ushort, int> ColorUsage = new();
    public List<PartIssue> Issues = new();
    public int Beads => Cells.Count;
    public Int3 Size => Max - Min + new Int3(1, 1, 1);

    /// <summary>板面二维尺寸（列 × 行）。</summary>
    public (int Cols, int Rows) SheetSize => PlaneAxis switch
    {
        0 => (Size.Z, Size.Y),
        1 => (Size.X, Size.Z),
        2 => (Size.X, Size.Y),
        _ => (Size.X, Size.Y),
    };
}

public sealed class ModelPartsReport
{
    public List<PartReport> Parts = new();
    public int TotalBeads;
    public int AssignedBeads;
    public List<int> Unassigned = new();
    public bool Conserved => AssignedBeads + Unassigned.Count == TotalBeads;
    public int ErrorCount => Parts.Sum(p => p.Issues.Count(i => i.Level == IssueLevel.Error)) + (Unassigned.Count > 0 ? 1 : 0);
}

/// <summary>零件校验：是否单层平板、是否连通、是否太小、是否有漏分配的豆。</summary>
public static class PartAnalyzer
{
    public const int MinBeads = 4;

    public static ModelPartsReport Analyze(VoxelModel m, bool writeBackAxis = true)
    {
        var rep = new ModelPartsReport();
        var byId = new Dictionary<int, PartReport>();
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Colors[i] == 0) continue;
            rep.TotalBeads++;
            int pid = m.Parts[i];
            if (pid == 0) { rep.Unassigned.Add(i); continue; }
            rep.AssignedBeads++;
            if (!byId.TryGetValue(pid, out var pr))
            {
                var info = m.FindPart(pid);
                pr = new PartReport { Id = pid, Name = info?.Name ?? $"P{pid:000}", Min = new Int3(int.MaxValue, int.MaxValue, int.MaxValue), Max = new Int3(-1, -1, -1) };
                byId[pid] = pr;
            }
            var p = m.Pos(i);
            pr.Cells.Add(i);
            pr.Min = new Int3(Math.Min(pr.Min.X, p.X), Math.Min(pr.Min.Y, p.Y), Math.Min(pr.Min.Z, p.Z));
            pr.Max = new Int3(Math.Max(pr.Max.X, p.X), Math.Max(pr.Max.Y, p.Y), Math.Max(pr.Max.Z, p.Z));
            pr.ColorUsage[m.Colors[i]] = pr.ColorUsage.GetValueOrDefault(m.Colors[i]) + 1;
        }

        foreach (var pr in byId.Values.OrderBy(p => p.Id))
        {
            var size = pr.Size;
            var info = m.FindPart(pr.Id);
            // 平板轴：厚度为 1 的轴；多个轴都为 1（一条线/一颗豆）时优先用户指定，其次取面积最大的投影
            var flat = Enumerable.Range(0, 3).Where(a => size[a] == 1).ToList();
            if (flat.Count > 0)
            {
                pr.Planar = true;
                pr.PlaneAxis = info != null && info.PlaneAxis >= 0 && flat.Contains(info.PlaneAxis) ? info.PlaneAxis
                    : flat.OrderByDescending(a => Area(size, a)).First();
                pr.PlaneCoord = pr.Min[pr.PlaneAxis];
            }
            else
            {
                pr.Planar = false;
                pr.PlaneAxis = -1;
                pr.Issues.Add(new PartIssue(IssueLevel.Error, $"不是单层平板（{size.X}×{size.Y}×{size.Z}），无法一次熨烫"));
            }
            pr.Islands = CountIslands(m, pr.Cells);
            if (pr.Islands > 1) pr.Issues.Add(new PartIssue(IssueLevel.Error, $"分成了 {pr.Islands} 块互不相连的部分"));
            if (pr.Beads < MinBeads) pr.Issues.Add(new PartIssue(IssueLevel.Warning, $"只有 {pr.Beads} 颗，太小不易熨烫，建议合并到相邻零件"));
            else if (pr.Planar && IsThinStrip(m, pr)) pr.Issues.Add(new PartIssue(IssueLevel.Warning, $"1×{Math.Max(pr.SheetSize.Cols, pr.SheetSize.Rows)} 单排过长：熨烫和拼接时易掉豆断开，建议改切两排或并入相邻零件"));
            if (writeBackAxis && info != null) info.PlaneAxis = pr.PlaneAxis;
            rep.Parts.Add(pr);
        }
        return rep;
    }

    private static int Area(Int3 s, int axis) => axis switch { 0 => s.Y * s.Z, 1 => s.X * s.Z, _ => s.X * s.Y };

    public static int CountIslands(VoxelModel m, List<int> cells)
    {
        var set = new HashSet<int>(cells);
        var seen = new HashSet<int>();
        int islands = 0;
        var q = new Queue<int>();
        foreach (var c in cells)
        {
            if (seen.Contains(c)) continue;
            islands++;
            seen.Add(c); q.Enqueue(c);
            while (q.Count > 0)
            {
                var p = m.Pos(q.Dequeue());
                foreach (var d in Dir6.All)
                {
                    var n = p + d;
                    if (!m.InBounds(n)) continue;
                    int j = m.Index(n);
                    if (set.Contains(j) && seen.Add(j)) q.Enqueue(j);
                }
            }
        }
        return islands;
    }

    private static bool IsThinStrip(VoxelModel m, PartReport pr)
    {
        var (c, r) = pr.SheetSize;
        return Math.Min(c, r) == 1 && Math.Max(c, r) >= 4;
    }

    /// <summary>零件的二维板面：返回 rows×cols 的模型调色板下标矩阵（0 = 空）。row 0 在上方。</summary>
    public static ushort[,] Sheet(VoxelModel m, PartReport pr, bool flip = false)
    {
        int axis = pr.PlaneAxis >= 0 ? pr.PlaneAxis : 2;
        var size = pr.Size;
        var (cols, rows) = axis switch
        {
            0 => (size.Z, size.Y),
            1 => (size.X, size.Z),
            _ => (size.X, size.Y),
        };
        var g = new ushort[rows, cols];
        foreach (var i in pr.Cells)
        {
            var p = m.Pos(i) - pr.Min;
            // 投影约定：从该轴正方向看过去（与成品外侧视角一致）
            var (c, r) = axis switch
            {
                0 => (size.Z - 1 - p.Z, size.Y - 1 - p.Y),   // 从 +X 看：右手边是 -Z
                1 => (p.X, p.Z),                              // 从 +Y 俯视：下方是 +Z（正面）
                _ => (p.X, size.Y - 1 - p.Y),                 // 从 +Z 正视
            };
            if (flip) c = cols - 1 - c;
            g[r, c] = m.Colors[i];
        }
        return g;
    }
}
