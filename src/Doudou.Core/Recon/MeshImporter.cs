using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
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
        var occ = Voxelizer.Voxelize(mesh, opt.TargetHeight);
        say?.Invoke($"体素：{occ.SX}×{occ.SY}×{occ.SZ}，{occ.Count():N0} 颗");

        var texColors = SampleTexture(mesh);
        VoxelModel model;
        bool usedTexture = false, usedViews = false;
        if (texColors != null)
        {
            say?.Invoke("使用模型自带纹理上色…");
            var colors = ColorFromVertices(mesh, occ, texColors, say, ct);
            model = Quantizer.Build(occ, colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedTexture = true;
        }
        else if (opt.Views.Count > 0)
        {
            say?.Invoke("模型无纹理，使用参考视图投影上色…");
            var aligned = ReconstructPipeline.BestOrientation(occ, opt.Views, say);
            var proj = ColorProjector.Project(aligned, opt.Views);
            model = Quantizer.Build(aligned, proj.Colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedViews = true;
        }
        else
        {
            say?.Invoke("模型无纹理，也没有参考视图：生成单色体素。");
            model = Quantizer.Build(occ, new Lab?[occ.Cells.Length], lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
        }

        int cleaned = SurfaceColorCleaner.Clean(model);
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
        if (ex > ey * 1.15f && ex >= ez)
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

    /// <summary>把顶点颜色按最近顶点分配给体素。KD-tree 保证每个体素都有命中，避免大三角形/平坦面漏色。</summary>
    private static Lab?[] ColorFromVertices(TriMesh mesh, Occupancy occ, Rgb[] vertexColors, Action<string>? say, CancellationToken ct)
    {
        var (mn, mx) = mesh.Bounds();
        float scale = occ.SY / (mx.Y - mn.Y);
        int nv = mesh.Vertices.Count;
        var pts = new Vector3[nv];
        for (int i = 0; i < nv; i++) pts[i] = (mesh.Vertices[i] - mn) * scale;
        var tree = new KdTree(pts);

        var colors = new Lab?[occ.Cells.Length];
        int found = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    if (!occ[x, y, z]) continue;
                    int vi = tree.Nearest(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
                    var rgb = vertexColors[vi];
                    colors[occ.Index(x, y, z)] = ColorMath.ToLab(rgb.R, rgb.G, rgb.B);
                    found++;
                    if (found % 25000 == 0) ct.ThrowIfCancellationRequested();
                }
        say?.Invoke($"纹理取色：{found:N0} 颗全部命中。");
        return colors;
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
