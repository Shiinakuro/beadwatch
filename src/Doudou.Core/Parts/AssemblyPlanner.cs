using System.Diagnostics;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Parts;

public enum StepStatus { Base, Verified, Complex }

public sealed class AssemblyStep
{
    public int Index;
    public int PartId;
    public string PartName = "";
    public StepStatus Status;
    /// <summary>零件沿哪个方向「装进来」（与离开方向相反）。-1 = 基准件 / 无直线路径。</summary>
    public int InsertDir = -1;
    public List<int> AttachedTo = new();
    public int ContactFaces;
    public string Instruction = "";
}

public sealed class AssemblyPlan
{
    public List<AssemblyStep> Steps = new();
    public bool Complete;
    public int StatesExplored;
    public bool SearchLimitReached;
    /// <summary>true = 回溯搜索找到了全程可验证的顺序；false = 贪心顺序。</summary>
    public bool Backtracked;
    public double Seconds;
    public int VerifiedCount => Steps.Count(s => s.Status == StepStatus.Verified);
    public int ComplexCount => Steps.Count(s => s.Status == StepStatus.Complex);
    public string Summary => ComplexCount == 0
        ? $"共 {Steps.Count} 步，全部装配路径已验证"
        : $"共 {Steps.Count} 步，{VerifiedCount} 步路径已验证，{ComplexCount} 步需要试装或点胶";
}

/// <summary>
/// 装配顺序规划：每一步加入一个零件，要求
///   1) 贴住至少一个已装零件；
///   2) 存在一个带符号轴方向，零件沿该方向整体平移到无穷远的途中不会撞到已装零件（直线可装入）。
/// 回溯 DFS + 失败状态记忆 + 时间/状态上限。找不到直线路径的零件标为「复杂」（需要弹性形变或胶接），而不是假装能装。
/// </summary>
public static class AssemblyPlanner
{
    public static AssemblyPlan Plan(VoxelModel m, ModelPartsReport rep, int maxStates = 50000, double maxSeconds = 6)
    {
        var sw = Stopwatch.StartNew();
        var parts = rep.Parts.Where(p => p.Beads > 0).ToList();
        int n = parts.Count;
        var plan = new AssemblyPlan();
        if (n == 0) { plan.Complete = true; return plan; }
        var idxOf = new Dictionary<int, int>();
        for (int k = 0; k < n; k++) idxOf[parts[k].Id] = k;
        var owner = new int[m.Count];
        Array.Fill(owner, -1);
        for (int k = 0; k < n; k++) foreach (var c in parts[k].Cells) owner[c] = k;

        // 邻接（接触面数）
        var contact = new Dictionary<int, int>[n];
        for (int k = 0; k < n; k++) contact[k] = new Dictionary<int, int>();
        for (int k = 0; k < n; k++)
            foreach (var c in parts[k].Cells)
            {
                var p = m.Pos(c);
                foreach (var d in Dir6.All)
                {
                    var q = p + d;
                    if (!m.InBounds(q)) continue;
                    int o = owner[m.Index(q)];
                    if (o >= 0 && o != k) contact[k][o] = contact[k].GetValueOrDefault(o) + 1;
                }
            }

        // 自由方向缓存：零件 k 沿 dir 离开时会撞到的零件集合
        var blockers = new HashSet<int>[n, 6];
        for (int k = 0; k < n; k++)
            for (int d = 0; d < 6; d++)
            {
                var set = new HashSet<int>();
                var dv = Dir6.All[d];
                foreach (var c in parts[k].Cells)
                {
                    var p = m.Pos(c) + dv;
                    while (m.InBounds(p))
                    {
                        int o = owner[m.Index(p)];
                        if (o >= 0 && o != k) set.Add(o);
                        p += dv;
                    }
                }
                blockers[k, d] = set;
            }

        // 连通分量：允许每个分量各有一个基准件
        var comp = new int[n];
        Array.Fill(comp, -1);
        int compCount = 0;
        for (int k = 0; k < n; k++)
        {
            if (comp[k] >= 0) continue;
            var st = new Stack<int>(); st.Push(k); comp[k] = compCount;
            while (st.Count > 0)
            {
                int x = st.Pop();
                foreach (var y in contact[x].Keys) if (comp[y] < 0) { comp[y] = compCount; st.Push(y); }
            }
            compCount++;
        }

        var placed = new bool[n];
        var order = new List<(int Part, int Dir, StepStatus St)>();
        var failed = new HashSet<string>();
        int states = 0;
        bool limit = false;

        int FreeDir(int k)
        {
            // 优先竖直向下装（-Y 方向装入 = 沿 +Y 离开），再水平
            int[] pref = { 2, 4, 5, 0, 1, 3 };
            foreach (var d in pref)
            {
                bool ok = true;
                foreach (var b in blockers[k, d]) if (placed[b]) { ok = false; break; }
                if (ok) return d;
            }
            return -1;
        }

        string Key() { var sb = new char[n]; for (int i = 0; i < n; i++) sb[i] = placed[i] ? '1' : '0'; return new string(sb); }

        bool Dfs()
        {
            if (order.Count == n) return true;
            if (++states > maxStates || sw.Elapsed.TotalSeconds > maxSeconds) { limit = true; return false; }
            var key = Key();
            if (failed.Contains(key)) return false;

            var cands = new List<(int K, int Dir, int Touch)>();
            var placedComps = new HashSet<int>();
            for (int k = 0; k < n; k++) if (placed[k]) placedComps.Add(comp[k]);
            for (int k = 0; k < n; k++)
            {
                if (placed[k]) continue;
                int touch = 0;
                foreach (var (o, c) in contact[k]) if (placed[o]) touch += c;
                if (touch == 0) continue;
                int dir = FreeDir(k);
                if (dir >= 0) cands.Add((k, dir, touch));
            }
            // 接触越多越先装（结构更稳），同等时小件优先
            foreach (var (k, dir, _) in cands.OrderByDescending(c => c.Touch).ThenBy(c => parts[c.K].Beads))
            {
                placed[k] = true; order.Add((k, dir, StepStatus.Verified));
                if (Dfs()) return true;
                placed[k] = false; order.RemoveAt(order.Count - 1);
                if (limit) return false;
            }
            // 没有候选：开新的连通分量（基准件 = 该分量最大、最靠下的件）
            if (cands.Count == 0)
            {
                var fresh = Enumerable.Range(0, n).Where(k => !placed[k] && !placedComps.Contains(comp[k]))
                    .OrderBy(k => parts[k].Min.Y).ThenByDescending(k => parts[k].Beads).Take(2).ToList();
                foreach (var k in fresh)
                {
                    placed[k] = true; order.Add((k, -1, StepStatus.Base));
                    if (Dfs()) return true;
                    placed[k] = false; order.RemoveAt(order.Count - 1);
                    if (limit) return false;
                }
            }
            failed.Add(key);
            return false;
        }

        // 零件很多时回溯代价太高，直接用贪心（每步仍做真实的扫掠验证）
        bool ok = n <= 80 && Dfs();
        if (!ok)
        {
            // 退化：贪心补全，装不进去的件标为「复杂」
            Array.Fill(placed, false);
            order.Clear();
            while (order.Count < n)
            {
                int best = -1, bestTouch = -1, bestDir = -1;
                for (int k = 0; k < n; k++)
                {
                    if (placed[k]) continue;
                    int touch = 0;
                    foreach (var (o, c) in contact[k]) if (placed[o]) touch += c;
                    int dir = FreeDir(k);
                    int score = touch * 2 + (dir >= 0 ? 1 : 0);
                    if (order.Count == 0) score = -parts[k].Min.Y * 1000 + parts[k].Beads;
                    if (score > bestTouch) { bestTouch = score; best = k; bestDir = dir; }
                }
                int t2 = 0;
                foreach (var (o, c) in contact[best]) if (placed[o]) t2 += c;
                var stt = order.Count == 0 || t2 == 0 ? StepStatus.Base : bestDir >= 0 ? StepStatus.Verified : StepStatus.Complex;
                placed[best] = true;
                order.Add((best, stt == StepStatus.Base ? -1 : bestDir, stt));
            }
        }

        Array.Fill(placed, false);
        for (int s = 0; s < order.Count; s++)
        {
            var (k, dir, st) = order[s];
            var step = new AssemblyStep
            {
                Index = s + 1,
                PartId = parts[k].Id,
                PartName = parts[k].Name,
                Status = st,
                InsertDir = dir < 0 ? -1 : dir ^ 1,
            };
            foreach (var (o, c) in contact[k])
                if (placed[o]) { step.AttachedTo.Add(parts[o].Id); step.ContactFaces += c; }
            step.Instruction = Describe(step, parts, parts[k]);
            plan.Steps.Add(step);
            placed[k] = true;
        }
        plan.Complete = true;
        plan.Backtracked = ok;
        plan.StatesExplored = states;
        plan.SearchLimitReached = limit;
        plan.Seconds = sw.Elapsed.TotalSeconds;
        return plan;
    }

    private static string Describe(AssemblyStep s, List<PartReport> parts, PartReport me)
    {
        string names = string.Join("、", s.AttachedTo.Select(id => parts.First(p => p.Id == id).Name));
        return s.Status switch
        {
            StepStatus.Base => $"以 {me.Name} 为基准平放（{SplitTools.AxisChinese(me.PlaneAxis)}，{me.Beads} 颗）",
            StepStatus.Verified => $"将 {me.Name} {Dir6.ChineseName(s.InsertDir)}装入，贴合 {names}（接触 {s.ContactFaces} 面）",
            _ => $"{me.Name} 无直线装入路径，需与 {names} 试装或点胶固定",
        };
    }
}
