using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Parts;

/// <summary>
/// 拆件辅助工具。原则：拆件由用户主导，这些工具只做「一键初稿」和「批量体力活」，
/// 全部经过 EditHistory，可撤销。
/// </summary>
public static class SplitTools
{
    public static string AxisPrefix(int axis) => axis switch { 0 => "S", 1 => "L", 2 => "F", _ => "P" };
    public static string AxisChinese(int axis) => axis switch { 0 => "侧板", 1 => "层板", 2 => "前后板", _ => "异形件" };

    /// <summary>
    /// 沿某轴逐层切：每层里的每个连通块成为一个零件。
    /// cells 为空 = 整个模型；否则只处理这些格（例如当前选区）。
    /// </summary>
    public static int SliceByAxis(VoxelModel m, EditHistory h, int axis, IReadOnlyCollection<int>? cells = null)
    {
        var set = cells != null ? new HashSet<int>(cells.Where(i => m.Colors[i] != 0)) : new HashSet<int>(m.SolidIndices());
        if (set.Count == 0) return 0;
        h.Begin($"按{Dir6.AxisName(axis)}轴分层拆件");
        int created = 0;
        var seen = new HashSet<int>();
        foreach (var start in set.OrderBy(i => m.Pos(i)[axis]))
        {
            if (seen.Contains(start)) continue;
            var comp = Brushes.Plane(m, m.Pos(start), axis, samePart: false, allow: i => set.Contains(i) && !seen.Contains(i));
            var part = m.NewPart();
            part.PlaneAxis = axis;
            foreach (var p in comp)
            {
                int i = m.Index(p);
                seen.Add(i);
                h.SetPart(i, (ushort)part.Id);
            }
            created++;
        }
        m.PruneEmptyParts();
        h.Commit();
        return created;
    }

    /// <summary>
    /// 面板式智能拆分（适合空心壳体，类似真人图纸里「前板 + 后板 + 侧板 + 顶底板」的箱体拆法）：
    /// 每颗豆归入它露出面所在的、面积最大的那块平面；内部豆跟随相邻零件；最后合并碎件。
    /// </summary>
    public static int PanelSplit(VoxelModel m, EditHistory h, int minBeads = PartAnalyzer.MinBeads)
    {
        int n = m.Count;
        // 1) 每个轴、每个朝外露面的平面连通区面积
        var regionSize = new int[3][];
        for (int a = 0; a < 3; a++)
        {
            regionSize[a] = new int[n];
            var label = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (m.Colors[i] == 0 || label[i] || !Exposed(m, i, a)) continue;
                var comp = Brushes.Plane(m, m.Pos(i), a, samePart: false, allow: j => !label[j] && Exposed(m, j, a));
                foreach (var p in comp) label[m.Index(p)] = true;
                foreach (var p in comp) regionSize[a][m.Index(p)] = comp.Count;
            }
        }
        // 2) 每颗豆选轴
        var choice = new int[n];
        Array.Fill(choice, -1);
        for (int i = 0; i < n; i++)
        {
            if (m.Colors[i] == 0) continue;
            int best = -1, bestSize = 0;
            for (int a = 0; a < 3; a++)
                if (regionSize[a][i] > bestSize) { bestSize = regionSize[a][i]; best = a; }
            choice[i] = best;
        }
        h.Begin("面板式智能拆分");
        for (int i = 0; i < n; i++) if (m.Colors[i] != 0) h.SetPart(i, 0);
        m.PartList.Clear();

        // 3) 同轴同层连通 → 零件
        var assigned = new bool[n];
        for (int a = 0; a < 3; a++)
            for (int i = 0; i < n; i++)
            {
                if (m.Colors[i] == 0 || assigned[i] || choice[i] != a) continue;
                var comp = Brushes.Plane(m, m.Pos(i), a, samePart: false, allow: j => !assigned[j] && choice[j] == a);
                var part = m.NewPart();
                part.PlaneAxis = a;
                foreach (var p in comp) { int j = m.Index(p); assigned[j] = true; h.SetPart(j, (ushort)part.Id); }
            }
        // 4) 内部豆（没露面）：并入同层相邻的零件；没有就单独分层
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int i = 0; i < n; i++)
            {
                if (m.Colors[i] == 0 || assigned[i]) continue;
                var p = m.Pos(i);
                foreach (var d in Dir6.All)
                {
                    var q = p + d;
                    if (!m.InBounds(q)) continue;
                    int j = m.Index(q);
                    if (!assigned[j]) continue;
                    var part = m.FindPart(m.Parts[j]);
                    if (part == null || part.PlaneAxis < 0 || q[part.PlaneAxis] != p[part.PlaneAxis]) continue;
                    h.SetPart(i, m.Parts[j]); assigned[i] = true; progress = true;
                    break;
                }
            }
        }
        var rest = Enumerable.Range(0, n).Where(i => m.Colors[i] != 0 && !assigned[i]).ToList();
        h.Commit();
        if (rest.Count > 0) SliceByAxis(m, h, 1, rest);
        MergeTiny(m, h, minBeads);
        Renumber(m, h);
        return m.PartList.Count;
    }

    private static bool Exposed(VoxelModel m, int i, int axis)
    {
        var p = m.Pos(i);
        return !m.Solid(p + Dir6.All[axis * 2]) || !m.Solid(p + Dir6.All[axis * 2 + 1]);
    }

    /// <summary>
    /// 分层环片拆分（适合人物、动物等圆润造型，对应真人图纸里一圈圈的环形层片）：
    /// 沿 axis 逐层切 → 合并碎件 → 重新编号。
    /// </summary>
    public static int LayerSplit(VoxelModel m, EditHistory h, int axis = 1, int minBeads = PartAnalyzer.MinBeads)
    {
        h.Begin("清空零件");
        for (int i = 0; i < m.Count; i++) if (m.Colors[i] != 0) h.SetPart(i, 0);
        m.PartList.Clear();
        h.Commit();
        SliceByAxis(m, h, axis);
        MergeTiny(m, h, minBeads);
        ResliceSmall(m, h, minBeads);
        Renumber(m, h);
        return m.PartList.Count;
    }

    /// <summary>
    /// 细小结构（挂环、手指、尾尖）按当前方向切会碎成单颗：把小零件聚成团，换个方向重切，碎件更少就采用。
    /// </summary>
    public static int ResliceSmall(VoxelModel m, EditHistory h, int minBeads = PartAnalyzer.MinBeads)
    {
        var rep = PartAnalyzer.Analyze(m);
        var small = rep.Parts.Where(p => p.Beads < minBeads * 2).ToDictionary(p => p.Id);
        if (!small.Values.Any(p => p.Beads < minBeads)) return 0;
        var cellsOf = small.Values.SelectMany(p => p.Cells).ToHashSet();
        var done = new HashSet<int>();
        int improved = 0;
        foreach (var seed in cellsOf.ToList())
        {
            if (done.Contains(seed)) continue;
            // 3D 连通的「小零件团」
            var cluster = new List<int>();
            var q = new Queue<int>(); q.Enqueue(seed); done.Add(seed);
            while (q.Count > 0)
            {
                int i = q.Dequeue(); cluster.Add(i);
                var p = m.Pos(i);
                foreach (var d in Dir6.All)
                {
                    var n = p + d;
                    if (!m.InBounds(n)) continue;
                    int j = m.Index(n);
                    if (cellsOf.Contains(j) && done.Add(j)) q.Enqueue(j);
                }
            }
            int TinyCount(IEnumerable<List<int>> pieces) => pieces.Count(x => x.Count < minBeads);
            var currentPieces = cluster.GroupBy(i => m.Parts[i]).Select(g => g.ToList()).ToList();
            int bestTiny = TinyCount(currentPieces), bestAxis = -1;
            for (int a = 0; a < 3; a++)
            {
                var pieces = PiecesAlong(m, cluster, a);
                int t = TinyCount(pieces);
                if (t < bestTiny || (t == bestTiny && bestAxis >= 0 && pieces.Count < currentPieces.Count)) { bestTiny = t; bestAxis = a; }
            }
            if (bestAxis < 0) continue;
            SliceByAxis(m, h, bestAxis, cluster);
            improved++;
        }
        if (improved > 0) MergeTiny(m, h, minBeads);
        return improved;
    }

    private static List<List<int>> PiecesAlong(VoxelModel m, List<int> cells, int axis)
    {
        var set = cells.ToHashSet();
        var seen = new HashSet<int>();
        var res = new List<List<int>>();
        foreach (var c in cells)
        {
            if (seen.Contains(c)) continue;
            var comp = Brushes.Plane(m, m.Pos(c), axis, samePart: false, allow: j => set.Contains(j) && !seen.Contains(j));
            var list = comp.Select(m.Index).ToList();
            foreach (var j in list) seen.Add(j);
            res.Add(list);
        }
        return res;
    }

    /// <summary>
    /// 推荐拆法：表面中落在大块平面（≥30 颗共面露面）里的比例高 → 箱体，用面板拆；否则用分层环片。
    /// </summary>
    public static (string Name, double Flatness) Recommend(VoxelModel m)
    {
        int surface = 0, flat = 0;
        var big = new bool[m.Count];
        for (int a = 0; a < 3; a++)
        {
            var seen = new bool[m.Count];
            for (int i = 0; i < m.Count; i++)
            {
                if (m.Colors[i] == 0 || seen[i] || !Exposed(m, i, a)) continue;
                var comp = Brushes.Plane(m, m.Pos(i), a, samePart: false, allow: j => !seen[j] && Exposed(m, j, a));
                foreach (var p in comp) { int j = m.Index(p); seen[j] = true; if (comp.Count >= 30) big[j] = true; }
            }
        }
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Colors[i] == 0) continue;
            bool exposed = Exposed(m, i, 0) || Exposed(m, i, 1) || Exposed(m, i, 2);
            if (!exposed) continue;
            surface++;
            if (big[i]) flat++;
        }
        double f = surface == 0 ? 0 : (double)flat / surface;
        return (f >= 0.7 ? "panel" : "layer", f);
    }

    /// <summary>把小于 minBeads 的零件并入「同轴同层」的相邻零件（保持平板）。返回合并次数。</summary>
    public static int MergeTiny(VoxelModel m, EditHistory h, int minBeads = PartAnalyzer.MinBeads)
    {
        h.Begin("合并碎件");
        int merged = 0;
        bool again = true;
        while (again)
        {
            again = false;
            var rep = PartAnalyzer.Analyze(m);
            foreach (var pr in rep.Parts.Where(p => p.Beads < minBeads).OrderBy(p => p.Beads))
            {
                // 候选：接触面最多、且合并后仍为单层平板的邻居
                var contact = new Dictionary<int, int>();
                foreach (var i in pr.Cells)
                {
                    var p = m.Pos(i);
                    foreach (var d in Dir6.All)
                    {
                        var q = p + d;
                        if (!m.InBounds(q)) continue;
                        int j = m.Index(q);
                        int pid = m.Parts[j];
                        if (pid == 0 || pid == pr.Id || m.Colors[j] == 0) continue;
                        contact[pid] = contact.GetValueOrDefault(pid) + 1;
                    }
                }
                foreach (var (pid, _) in contact.OrderByDescending(kv => kv.Value))
                {
                    var other = rep.Parts.FirstOrDefault(x => x.Id == pid);
                    if (other == null || other.PlaneAxis < 0) continue;
                    int a = other.PlaneAxis;
                    if (pr.Cells.All(i => m.Pos(i)[a] == other.PlaneCoord))
                    {
                        foreach (var i in pr.Cells) h.SetPart(i, (ushort)pid);
                        merged++; again = true;
                        break;
                    }
                }
                if (again) break;
            }
        }
        m.PruneEmptyParts();
        h.Commit();
        return merged;
    }

    /// <summary>
    /// 两块互相垂直、沿一条棱相接的平板之间做「齿口卡扣」：
    /// 把棱上的豆按 tooth 颗一段，交替分给两块板，装配时像榫卯一样咬合（真人图纸里的锯齿边）。
    /// </summary>
    public static int FingerJoint(VoxelModel m, EditHistory h, int partA, int partB, int tooth = 2)
    {
        var rep = PartAnalyzer.Analyze(m);
        var a = rep.Parts.FirstOrDefault(p => p.Id == partA);
        var b = rep.Parts.FirstOrDefault(p => p.Id == partB);
        if (a == null || b == null || a.PlaneAxis < 0 || b.PlaneAxis < 0 || a.PlaneAxis == b.PlaneAxis) return 0;
        int lineAxis = 3 - a.PlaneAxis - b.PlaneAxis;
        // 棱上的格：属于其中一块、同时位于另一块所在平面上、并与另一块相邻
        var edge = new List<int>();
        void Collect(PartReport owner, PartReport other)
        {
            foreach (var i in owner.Cells)
            {
                var p = m.Pos(i);
                if (p[other.PlaneAxis] != other.PlaneCoord) continue;
                bool touches = false;
                foreach (var d in Dir6.All)
                {
                    var q = p + d;
                    if (m.InBounds(q) && m.Parts[m.Index(q)] == other.Id) { touches = true; break; }
                }
                if (touches) edge.Add(i);
            }
        }
        Collect(a, b); Collect(b, a);
        if (edge.Count < 2) return 0;
        edge.Sort((x, y) => m.Pos(x)[lineAxis].CompareTo(m.Pos(y)[lineAxis]));
        h.Begin($"齿口卡扣 {a.Name}↔{b.Name}");
        int changed = 0;
        int start = m.Pos(edge[0])[lineAxis];
        foreach (var i in edge)
        {
            int k = (m.Pos(i)[lineAxis] - start) / Math.Max(1, tooth);
            ushort want = (ushort)(k % 2 == 0 ? partA : partB);
            if (m.Parts[i] != want) { h.SetPart(i, want); changed++; }
        }
        // 齿口不能把任何一块板切断：否则整组撤销
        foreach (var pid in new[] { partA, partB })
        {
            var cells = new List<int>();
            for (int i = 0; i < m.Count; i++) if (m.Parts[i] == pid && m.Colors[i] != 0) cells.Add(i);
            if (PartAnalyzer.CountIslands(m, cells) > 1) { h.Cancel(); return 0; }
        }
        h.Commit();
        return changed;
    }

    /// <summary>对所有「垂直相接」的零件对自动做齿口。返回处理的零件对数。</summary>
    public static int FingerJointAll(VoxelModel m, EditHistory h, int tooth = 2, int minEdge = 3)
    {
        var rep = PartAnalyzer.Analyze(m);
        var pairs = new HashSet<(int, int)>();
        foreach (var pr in rep.Parts.Where(p => p.PlaneAxis >= 0))
            foreach (var i in pr.Cells)
            {
                var p = m.Pos(i);
                foreach (var d in Dir6.All)
                {
                    var q = p + d;
                    if (!m.InBounds(q)) continue;
                    int pid = m.Parts[m.Index(q)];
                    if (pid == 0 || pid == pr.Id) continue;
                    var o = rep.Parts.FirstOrDefault(x => x.Id == pid);
                    if (o == null || o.PlaneAxis < 0 || o.PlaneAxis == pr.PlaneAxis) continue;
                    pairs.Add((Math.Min(pr.Id, pid), Math.Max(pr.Id, pid)));
                }
            }
        int done = 0;
        foreach (var (x, y) in pairs)
            if (FingerJoint(m, h, x, y, tooth) >= minEdge / 2) done++;
        return done;
    }

    /// <summary>按「板类型 → 所在层 → 位置」排序并重新命名（S=侧板 L=层板 F=前后板）。</summary>
    public static void Renumber(VoxelModel m, EditHistory h)
    {
        var rep = PartAnalyzer.Analyze(m);
        h.Begin("重新编号零件");
        var order = rep.Parts
            .OrderBy(p => p.PlaneAxis < 0 ? 9 : p.PlaneAxis == 1 ? 0 : p.PlaneAxis == 2 ? 1 : 2)
            .ThenByDescending(p => p.PlaneAxis >= 0 ? (p.PlaneAxis == 1 ? -p.PlaneCoord : p.PlaneCoord) : 0)
            .ThenBy(p => p.Min.X).ThenBy(p => p.Min.Y).ThenBy(p => p.Min.Z).ToList();
        var counters = new Dictionary<string, int>();
        foreach (var pr in order)
        {
            var info = m.FindPart(pr.Id);
            if (info == null) continue;
            var prefix = AxisPrefix(pr.PlaneAxis);
            int k = counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
            info.Name = $"{prefix}{k:00}";
            info.PlaneAxis = pr.PlaneAxis;
        }
        // 零件列表按新顺序排列
        var sorted = order.Select(pr => m.FindPart(pr.Id)).Where(p => p != null).Cast<PartInfo>().ToList();
        m.PartList.Clear(); m.PartList.AddRange(sorted);
        h.Commit();
    }
}
