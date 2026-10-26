using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Imaging;
using Doudou.Core.IO;
using Doudou.Core.Model;

namespace Doudou.Core.Recon;

public sealed class MeshImportOptions
{
    public int TargetHeight { get; set; } = 64;
    public string BrandId { get; set; } = "mard221";
    public int MaxColors { get; set; } = 24;
    public string Name { get; set; } = "导入模型";
    /// <summary>可选参考视图：无纹理时用于投影上色。</summary>
    public IReadOnlyList<ViewImage> Views { get; set; } = Array.Empty<ViewImage>();
    /// <summary>即使模型带纹理也优先用参考视图取色（AI 生成的纹理常带烘焙光照）。</summary>
    public bool PreferViews { get; set; }
    /// <summary>去除模型纹理/参考图里的烘焙光照，只保留相对稳定的底色。</summary>
    public bool RemoveLighting { get; set; } = true;
    /// <summary>只压平纹理里同色相的高光/阴影，保留眼睛、描边等异色细节。</summary>
    public bool FlattenTextureColors { get; set; } = true;
}

public sealed class MeshImportResult
{
    public VoxelModel Model { get; init; } = null!;
    public bool UsedTexture { get; init; }
    public bool UsedViews { get; init; }
    public bool RotatedZUp { get; init; }
}

/// <summary>
/// 导入 OBJ/GLB/STL：自动把 Z-up 转成 Y-up，按目标豆高体素化；
/// 带 UV+纹理的 GLB/OBJ 直接用纹理上色，否则用参考视图投影，最后量化到色卡。
/// </summary>
public static class MeshImporter
{
    public static MeshImportResult Import(TriMesh mesh, PaletteLibrary lib, MeshImportOptions opt,
        Action<string>? say = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool rotated = OrientMesh(mesh, opt.Views, opt.TargetHeight, say);
        say?.Invoke($"网格 {mesh.TriangleCount:N0} 个三角面，体素化到 {opt.TargetHeight} 颗高…");
        var occ = Voxelizer.Voxelize(mesh, opt.TargetHeight, out var frame);
        say?.Invoke($"体素：{occ.SX}×{occ.SY}×{occ.SZ}，{occ.Count():N0} 颗");

        var texColors = SampleTexture(mesh);
        if (opt.Views.Count > 0 && opt.PreferViews)
        {
            // 只有明确要求用参考图取色时才预处理参考图；纹理路径不再做无用的平涂。
            foreach (var v in opt.Views)
                if (!v.IsAi) v.FlattenWith(null);
        }
        VoxelModel model;
        bool usedTexture = false, usedViews = false;
        if (texColors != null && !(opt.PreferViews && opt.Views.Count > 0))
        {
            say?.Invoke("使用模型自带纹理上色…");
            var colors = MeshTextureSampler.Sample(mesh, occ, frame, say, ct)
                         ?? ColorFromVertices(mesh, occ, frame, texColors, say, ct);
            FillMissingFromVertices(mesh, occ, frame, texColors, colors, say, ct);
            if (opt.FlattenTextureColors)
            {
                // 只压平同一材质上的高光/阴影，保留眼睛、嘴唇、花纹等异色细节。
                colors = TextureDeLight(occ, colors, say);
            }
            model = Quantizer.Build(occ, colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedTexture = true;
        }
        else if (opt.Views.Count > 0)
        {
            say?.Invoke("模型无纹理，使用参考视图投影上色…");
            var aligned = ReconstructPipeline.BestOrientation(occ, opt.Views, say);
            var proj = ColorProjector.Project(aligned, opt.Views);
            var colors = opt.RemoveLighting ? SurfaceDeLight(aligned, proj.Colors, say) : proj.Colors;
            model = Quantizer.Build(aligned, colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedViews = true;
        }
        else
        {
            say?.Invoke("模型无纹理，也没有参考视图：生成单色体素。");
            model = Quantizer.Build(occ, new Lab?[occ.Cells.Length], lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
        }

        int cleaned = usedTexture ? 0 : SurfaceColorCleaner.Clean(model);
        if (cleaned > 0) model.CompactPalette();
        say?.Invoke($"上色完成：{model.Palette.Count - 1} 色，清理 {cleaned} 颗。");
        return new MeshImportResult { Model = model, UsedTexture = usedTexture, UsedViews = usedViews, RotatedZUp = rotated };
    }

    /// <summary>导入前检查朝向：有参考视图时用 6 个轴向 + 轮廓吻合度选最正的方向；没有视图时按最长轴自动转正。</summary>
    private static bool OrientMesh(TriMesh mesh, IReadOnlyList<ViewImage> views, int targetHeight, Action<string>? say)
    {
        var original = mesh.Vertices.ToArray();
        if (views.Count > 0)
        {
            var candidates = new (string Name, Func<Vector3, Vector3> F)[]
            {
                ("Y-up（无需旋转）", v => v),
                ("X+90°", v => new Vector3(v.X, -v.Z, v.Y)),
                ("X-90°", v => new Vector3(v.X, v.Z, -v.Y)),
                ("X 180°", v => new Vector3(v.X, -v.Y, -v.Z)),
                ("Z+90°", v => new Vector3(v.Y, -v.X, v.Z)),
                ("Z-90°", v => new Vector3(-v.Y, v.X, v.Z)),
            };
            double best = -1;
            int bestIndex = 0;
            for (int ci = 0; ci < candidates.Length; ci++)
            {
                for (int i = 0; i < original.Length; i++) mesh.Vertices[i] = candidates[ci].F(original[i]);
                var (mn, mx) = mesh.Bounds();
                if (mx.Y - mn.Y <= 1e-6f) continue;
                var occ = Voxelizer.Voxelize(mesh, Math.Min(targetHeight, 48));
                var aligned = ReconstructPipeline.BestOrientation(occ, views, null);
                double score = 0;
                foreach (var v in views)
                    score += SilhouetteFit.IoU(aligned, v) * (v.Kind == ViewKind.Front ? 1.5 : 1);
                if (score > best) { best = score; bestIndex = ci; }
            }
            for (int i = 0; i < original.Length; i++) mesh.Vertices[i] = candidates[bestIndex].F(original[i]);
            double avg = best / views.Sum(v => v.Kind == ViewKind.Front ? 1.5 : 1);
            say?.Invoke($"朝向检查：{candidates[bestIndex].Name}，轮廓吻合度 {avg:P0}。");
            if (avg < 0.5) say?.Invoke("朝向置信度偏低：导入后可在编辑器里旋转校正。");
            return bestIndex != 0;
        }

        var (bMin, bMax) = mesh.Bounds();
        float ex = bMax.X - bMin.X, ey = bMax.Y - bMin.Y, ez = bMax.Z - bMin.Z;
        if (ez > ey * 1.15f && ez >= ex)
        {
            mesh.Transform(v => new Vector3(v.X, v.Z, -v.Y));
            say?.Invoke("检测到 Z-up / 躺倒模型，已自动转正为 Y-up。");
            return true;
        }
        if (ex > ey * 1.5f && ex > ez * 1.5f)
        {
            mesh.Transform(v => new Vector3(-v.Y, v.X, v.Z));
            say?.Invoke("检测到 X-up 模型，已自动转正为 Y-up。");
            return true;
        }
        return false;
    }

    /// <summary>按顶点 UV 从 baseColor 纹理取色。UV 以左下角为原点，图片以左上角为原点。</summary>
    private static Rgb[]? SampleTexture(TriMesh mesh)
    {
        var tex = mesh.Texture;
        var uvs = mesh.VertexUVs;
        if (tex == null || uvs == null || uvs.Count != mesh.Vertices.Count) return null;
        var colors = new Rgb[uvs.Count];
        for (int i = 0; i < uvs.Count; i++)
        {
            float u = uvs[i].X - MathF.Floor(uvs[i].X);
            float v = uvs[i].Y - MathF.Floor(uvs[i].Y);
            int x = Math.Clamp((int)(u * (tex.W - 1) + 0.5f), 0, tex.W - 1);
            int y = Math.Clamp((int)((mesh.TextureFlipV ? 1f - v : v) * (tex.H - 1) + 0.5f), 0, tex.H - 1);
            colors[i] = tex.GetRgb(x, y);
        }
        return colors;
    }

    /// <summary>最简单的兜底：按最近顶点颜色填。精确采样已经覆盖绝大多数表面，这里只处理纹理缺失/断开的碎片。</summary>
    private static Lab?[] ColorFromVertices(TriMesh mesh, Occupancy occ, Voxelizer.VoxelFrame frame,
        Rgb[] vertexColors, Action<string>? say, CancellationToken ct)
    {
        var colors = new Lab?[occ.Cells.Length];
        FillMissingFromVertices(mesh, occ, frame, vertexColors, colors, say, ct);
        return colors;
    }

    private static void FillMissingFromVertices(TriMesh mesh, Occupancy occ, Voxelizer.VoxelFrame frame,
        Rgb[] vertexColors, Lab?[] colors, Action<string>? say, CancellationToken ct)
    {
        int missing = 0;
        for (int i = 0; i < occ.Cells.Length; i++)
            if (occ.Cells[i] && !colors[i].HasValue) missing++;
        if (missing == 0) return;

        var pts = new Vector3[mesh.Vertices.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = frame.ToVoxel(mesh.Vertices[i]);
        var tree = new KdTree(pts);

        int found = 0;
        for (int i = 0; i < occ.Cells.Length; i++)
        {
            if (!occ.Cells[i] || colors[i].HasValue) continue;
            int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
            int vi = tree.Nearest(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            var rgb = vertexColors[vi];
            colors[i] = ColorMath.ToLab(rgb.R, rgb.G, rgb.B);
            found++;
            if ((found & 8191) == 0) ct.ThrowIfCancellationRequested();
        }
        say?.Invoke($"纹理兜底：{found:N0} 颗未直接命中的豆按最近顶点补色。");
    }

    /// <summary>
    /// 纹理去光影：只把「同色相、同材质」邻域里的亮度离群点拉回中位亮度，保留 a/b 色相。
    /// 眼睛、嘴线、描边、花纹与周围色相差异大，会被直接跳过，不会被压成皮肤或头发色。
    /// </summary>
    private static Lab?[] TextureDeLight(Occupancy occ, Lab?[] colors, Action<string>? say)
    {
        int n = colors.Length;
        var surface = new bool[n];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    foreach (var d in Dir6.All)
                        if (!occ[x + d.X, y + d.Y, z + d.Z]) { surface[i] = true; break; }
                }

        const int R = 5;
        var result = (Lab?[])colors.Clone();
        var ls = new List<double>(128);
        int changed = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!surface[i] || colors[i] is not { } c) continue;
                    double cc = Math.Sqrt(c.A * c.A + c.B * c.B);
                    ls.Clear();
                    for (int dz = -R; dz <= R; dz++)
                        for (int dy = -R; dy <= R; dy++)
                            for (int dx = -R; dx <= R; dx++)
                            {
                                if ((dx | dy | dz) == 0 || dx * dx + dy * dy + dz * dz > R * R) continue;
                                int ax = x + dx, ay = y + dy, az = z + dz;
                                if (!occ.In(ax, ay, az)) continue;
                                int j = occ.Index(ax, ay, az);
                                if (!surface[j] || colors[j] is not { } o) continue;
                                if (!SameMaterial(c, cc, o)) continue;
                                ls.Add(o.L);
                            }
                    if (ls.Count < 8) continue;
                    ls.Sort();
                    double median = ls[ls.Count / 2];
                    if (Math.Abs(c.L - median) < 8) continue;
                    result[i] = new Lab(median, c.A, c.B);
                    changed++;
                }
        if (changed > 0) say?.Invoke($"纹理去光照：修正 {changed:N0} 颗表面豆的明暗（保留色相与细节）。");
        return result;
    }

    private static bool SameMaterial(Lab a, double ca, Lab b)
    {
        double cb = Math.Sqrt(b.A * b.A + b.B * b.B);
        if (Math.Abs(a.L - b.L) >= 40) return false;
        if (ca < 8 || cb < 8) return ca < 8 && cb < 8 && Math.Abs(a.L - b.L) < 22;
        if (Math.Abs(ca - cb) > 22) return false;
        double dot = (a.A * b.A + a.B * b.B) / (ca * cb);
        return dot >= Math.Cos(25 * Math.PI / 180);
    }

    /// <summary>
    /// 在体素表面做局部多数色平滑：把高光/阴影这类亮度离群点拉回周围的底色，
    /// 同时保留眼睛、描边、金色饰边等高饱和或深色的小细节。
    /// </summary>
    private static Lab?[] SurfaceDeLight(Occupancy occ, Lab?[] colors, Action<string>? say)
    {
        int n = colors.Length;
        var surface = new bool[n];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    for (int d = 0; d < 6; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        int nz = z + (d == 4 ? 1 : d == 5 ? -1 : 0);
                        if (!occ.In(nx, ny, nz) || !occ.Cells[occ.Index(nx, ny, nz)]) { surface[i] = true; break; }
                    }
                }

        const int R = 5;
        var result = (Lab?[])colors.Clone();
        var buckets = new Dictionary<(int, int, int), (int Count, double L, double A, double B)>();
        int changed = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!surface[i] || colors[i] is not { } center) continue;
                    buckets.Clear();
                    int total = 0, similar = 0;
                    double cl = center.L, ca = center.A, cb = center.B;
                    for (int dz = -R; dz <= R; dz++)
                        for (int dy = -R; dy <= R; dy++)
                            for (int dx = -R; dx <= R; dx++)
                            {
                                if (dx * dx + dy * dy + dz * dz > R * R || (dx | dy | dz) == 0) continue;
                                int ax = x + dx, ay = y + dy, az = z + dz;
                                if (!occ.In(ax, ay, az)) continue;
                                int j = occ.Index(ax, ay, az);
                                if (!surface[j] || colors[j] is not { } c) continue;
                                total++;
                                double dl = c.L - cl, da = c.A - ca, db = c.B - cb;
                                if (dl * dl + da * da + db * db < 400) similar++;
                                var key = ((int)Math.Round(c.L / 10), (int)Math.Round(c.A / 12), (int)Math.Round(c.B / 12));
                                buckets.TryGetValue(key, out var b);
                                buckets[key] = (b.Count + 1, b.L + c.L, b.A + c.A, b.B + c.B);
                            }
                    if (total < 8 || buckets.Count == 0) continue;
                    var dominant = buckets.OrderByDescending(kv => kv.Value.Count).First().Value;
                    if (dominant.Count < total * 0.25) continue;
                    double domL = dominant.L / dominant.Count, domA = dominant.A / dominant.Count, domB = dominant.B / dominant.Count;
                    double chroma = Math.Sqrt(center.A * center.A + center.B * center.B);
                    if (chroma > 45 && similar >= 4) continue; // gold trim, eyes, saturated details
                    bool highlight = center.L - domL > 5;
                    bool shadow = domL - center.L > 8 && center.L > 35;
                    if (!highlight && !shadow) continue;
                    result[i] = new Lab(domL, domA, domB);
                    changed++;
                }
        if (changed > 0) say?.Invoke($"去光照：修正 {changed:N0} 颗表面豆的明暗。");
        return result;
    }

    /// <summary>最小 3D KD-tree：对 87 万顶点做最近邻查询，避免体素漏色。</summary>
    private sealed class KdTree
    {
        private struct Node
        {
            public int Lo, Hi;     // leaf range [Lo,Hi]
            public int Pivot;      // internal split index
            public int Axis;       // -1 = leaf
            public int Left, Right;
        }

        private readonly Vector3[] _p;
        private readonly int[] _ids;
        private readonly List<Node> _nodes = new();

        public KdTree(Vector3[] points)
        {
            _p = points;
            _ids = Enumerable.Range(0, points.Length).ToArray();
            Build(0, _ids.Length, 0);
        }

        private int Build(int lo, int hi, int depth)
        {
            int node = _nodes.Count;
            _nodes.Add(default);
            if (hi - lo <= 8)
            {
                _nodes[node] = new Node { Lo = lo, Hi = hi, Axis = -1 };
                return node;
            }
            int axis = depth % 3;
            Array.Sort(_ids, lo, hi - lo, Comparer<int>.Create((a, b) => _p[a][axis].CompareTo(_p[b][axis])));
            int mid = (lo + hi) / 2;
            int left = Build(lo, mid, depth + 1);
            int right = Build(mid + 1, hi, depth + 1);
            _nodes[node] = new Node { Lo = lo, Hi = hi, Pivot = mid, Axis = axis, Left = left, Right = right };
            return node;
        }

        public int Nearest(Vector3 target)
        {
            int best = -1;
            float bestD = float.MaxValue;
            Search(0, target, ref best, ref bestD);
            return best;
        }

        private void Search(int node, Vector3 target, ref int best, ref float bestD)
        {
            var n = _nodes[node];
            if (n.Axis < 0)
            {
                for (int i = n.Lo; i < n.Hi; i++)
                {
                    float d = Vector3.DistanceSquared(target, _p[_ids[i]]);
                    if (d < bestD) { bestD = d; best = _ids[i]; }
                }
                return;
            }
            int pivotId = _ids[n.Pivot];
            float diff = target[n.Axis] - _p[pivotId][n.Axis];
            int near = diff <= 0 ? n.Left : n.Right;
            int far = diff <= 0 ? n.Right : n.Left;
            Search(near, target, ref best, ref bestD);
            if (diff * diff < bestD) Search(far, target, ref best, ref bestD);
        }
    }
}
