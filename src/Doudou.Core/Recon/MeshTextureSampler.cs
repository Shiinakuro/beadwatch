using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Imaging;
using Doudou.Core.IO;

namespace Doudou.Core.Recon;

/// <summary>
/// 把网格纹理精确映射到体素表面。
/// 每个表面豆沿外法线发射几条短射线，找到网格表面后按三角形重心坐标插值 UV。
/// 这比“找最近顶点”更能保住眼睛、描边、发饰边界等小面积细节。
/// </summary>
public static class MeshTextureSampler
{
    private const int LeafSize = 8;
    private const float RayLength = 3.0f;
    private static readonly Vector3[] Offsets = BuildOffsets();

    private static Vector3[] BuildOffsets()
    {
        var list = new List<Vector3>(9);
        foreach (var y in new[] { -0.34f, 0f, 0.34f })
            foreach (var x in new[] { -0.34f, 0f, 0.34f })
                list.Add(new Vector3(x, y, 0));
        return list.ToArray();
    }

    public static Lab?[]? Sample(TriMesh mesh, Occupancy occ, Voxelizer.VoxelFrame frame,
        Action<string>? say, CancellationToken ct)
    {
        var tex = mesh.Texture;
        var uvs = mesh.VertexUVs;
        if (tex == null || uvs == null || uvs.Count != mesh.Vertices.Count || mesh.Indices.Count < 3) return null;

        var pos = new Vector3[mesh.Vertices.Count];
        for (int i = 0; i < pos.Length; i++) pos[i] = frame.ToVoxel(mesh.Vertices[i]);
        var tris = mesh.Indices.ToArray();
        var bvh = new Bvh(pos, tris);

        int n = occ.Cells.Length;
        var colors = new Lab?[n];
        var normals = ColorProjector.EstimateNormals(occ, 2);
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

        var samples = new List<Lab>(Offsets.Length);
        int sampled = 0, missed = 0, rays = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!surface[i]) continue;
                    if ((sampled & 4095) == 0) ct.ThrowIfCancellationRequested();

                    var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    var normal = normals[i];
                    if (normal.LengthSquared() < 0.25f)
                    {
                        normal = Vector3.Zero;
                        foreach (var d in Dir6.All)
                            if (!occ[x + d.X, y + d.Y, z + d.Z])
                                normal -= new Vector3(d.X, d.Y, d.Z);
                    }
                    normal = normal.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(normal);

                    var reference = MathF.Abs(normal.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY;
                    var tangent = Vector3.Normalize(Vector3.Cross(normal, reference));
                    var bitangent = Vector3.Cross(normal, tangent);

                    samples.Clear();
                    foreach (var offset in Offsets)
                    {
                        var origin = center + tangent * offset.X + bitangent * offset.Y;
                        rays++;
                        bool hit = bvh.Hit(origin, normal, RayLength, out int tri, out float bu, out float bv, frontOnly: true);
                        if (!hit) hit = bvh.Hit(origin, normal, RayLength, out tri, out bu, out bv);
                        if (!hit) continue;
                        int k = tri * 3;
                        var uv0 = uvs[tris[k]];
                        var uv1 = uvs[tris[k + 1]];
                        var uv2 = uvs[tris[k + 2]];
                        var uv = uv0 * (1 - bu - bv) + uv1 * bu + uv2 * bv;
                        if (SampleUv(tex, uv, mesh.TextureFlipV) is { } lab) samples.Add(lab);
                    }

                    if (samples.Count > 0)
                    {
                        colors[i] = PickRepresentative(samples);
                        sampled++;
                    }
                    else missed++;
                }

        int filled = FillNeighbours(occ, colors);
        say?.Invoke($"纹理采样：{sampled:N0} 颗表面豆命中（{rays:N0} 条射线），{missed:N0} 颗回退，{filled:N0} 颗内部继承。");
        return colors;
    }

    /// <summary>把已取色的表面色向相邻实体扩散；只填空缺，不覆盖直接采样结果。</summary>
    private static int FillNeighbours(Occupancy occ, Lab?[] colors)
    {
        var queue = new Queue<int>();
        for (int i = 0; i < colors.Length; i++)
            if (colors[i].HasValue) queue.Enqueue(i);
        int filled = 0;
        while (queue.Count > 0)
        {
            int j = queue.Dequeue();
            int x = j % occ.SX, t = j / occ.SX, y = t % occ.SY, z = t / occ.SY;
            foreach (var d in Dir6.All)
            {
                int ax = x + d.X, ay = y + d.Y, az = z + d.Z;
                if (!occ.In(ax, ay, az)) continue;
                int k = occ.Index(ax, ay, az);
                if (!occ.Cells[k] || colors[k].HasValue) continue;
                colors[k] = colors[j];
                filled++;
                queue.Enqueue(k);
            }
        }
        return filled;
    }

    /// <summary>
    /// 细节优先的降采样：把 9 个采样点分成两簇。
    /// 少数簇只要占到约 1/5 且色差明显，就保留它——眼睛、嘴线、发饰边缘常只覆盖半个格子，
    /// 若永远选多数簇，这些小特征会在低豆高时整片消失。
    /// </summary>
    private static Lab PickRepresentative(List<Lab> px)
    {
        if (px.Count <= 2) return Mean(px);
        var a = px.MinBy(p => p.L);
        var b = px.MaxBy(p => p.L);
        var ca = new List<Lab>();
        var cb = new List<Lab>();
        for (int it = 0; it < 4; it++)
        {
            ca.Clear(); cb.Clear();
            foreach (var p in px) (Dist2(p, a) <= Dist2(p, b) ? ca : cb).Add(p);
            if (ca.Count == 0 || cb.Count == 0) return Mean(px);
            a = Mean(ca); b = Mean(cb);
        }
        double sep = ColorMath.DeltaE2000(a, b);
        if (sep < 16) return Mean(px);
        bool aMajor = ca.Count >= cb.Count;
        var major = aMajor ? a : b;
        var minor = aMajor ? b : a;
        int minorCount = aMajor ? cb.Count : ca.Count;
        double minorFrac = (double)minorCount / px.Count;
        if (minorFrac >= 0.18 && sep >= 20) return minor;
        return major;

        static double Dist2(Lab x, Lab y)
        {
            double dl = x.L - y.L, da = x.A - y.A, db = x.B - y.B;
            return dl * dl + da * da + db * db;
        }
    }

    private static Lab Mean(List<Lab> px)
    {
        double l = 0, a = 0, b = 0;
        foreach (var p in px) { l += p.L; a += p.A; b += p.B; }
        return new Lab(l / px.Count, a / px.Count, b / px.Count);
    }

    private static Lab? SampleUv(RgbaImage tex, Vector2 uv, bool flipV)
    {
        float u = uv.X - MathF.Floor(uv.X);
        float v = uv.Y - MathF.Floor(uv.Y);
        if (flipV) v = 1 - v;
        float fx = u * tex.W - 0.5f;
        float fy = v * tex.H - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        x0 = Math.Clamp(x0, 0, tex.W - 1);
        y0 = Math.Clamp(y0, 0, tex.H - 1);
        int x1 = Math.Clamp(x0 + 1, 0, tex.W - 1);
        int y1 = Math.Clamp(y0 + 1, 0, tex.H - 1);
        var c00 = tex.GetRgb(x0, y0);
        var c10 = tex.GetRgb(x1, y0);
        var c01 = tex.GetRgb(x0, y1);
        var c11 = tex.GetRgb(x1, y1);
        int r = (int)((c00.R * (1 - ax) + c10.R * ax) * (1 - ay) + (c01.R * (1 - ax) + c11.R * ax) * ay + 0.5f);
        int g = (int)((c00.G * (1 - ax) + c10.G * ax) * (1 - ay) + (c01.G * (1 - ax) + c11.G * ax) * ay + 0.5f);
        int b = (int)((c00.B * (1 - ax) + c10.B * ax) * (1 - ay) + (c01.B * (1 - ax) + c11.B * ax) * ay + 0.5f);
        return ColorMath.ToLab(r, g, b);
    }

    private sealed class Bvh
    {
        private struct Node
        {
            public Vector3 Min, Max;
            public int Left, Right;
            public int Start, Count;
            public bool Leaf => Count > 0;
        }

        private readonly Vector3[] _pos;
        private readonly int[] _tris;
        private readonly Vector3[] _centroids;
        private readonly int[] _order;
        private readonly List<Node> _nodes = new();

        public Bvh(Vector3[] pos, int[] tris)
        {
            _pos = pos;
            _tris = tris;
            int count = tris.Length / 3;
            _centroids = new Vector3[count];
            _order = new int[count];
            for (int t = 0; t < count; t++)
            {
                _order[t] = t;
                _centroids[t] = (pos[tris[t * 3]] + pos[tris[t * 3 + 1]] + pos[tris[t * 3 + 2]]) / 3;
            }
            if (count > 0) Build(0, count);
        }

        private int Build(int start, int count)
        {
            int id = _nodes.Count;
            _nodes.Add(default);
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            var cmn = new Vector3(float.MaxValue);
            var cmx = new Vector3(float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                var c = _centroids[t];
                cmn = Vector3.Min(cmn, c);
                cmx = Vector3.Max(cmx, c);
                for (int k = 0; k < 3; k++)
                {
                    var p = _pos[_tris[t * 3 + k]];
                    mn = Vector3.Min(mn, p);
                    mx = Vector3.Max(mx, p);
                }
            }

            if (count <= LeafSize)
            {
                _nodes[id] = new Node { Min = mn, Max = mx, Start = start, Count = count };
                return id;
            }

            var e = cmx - cmn;
            int axis = e.Y > e.X && e.Y >= e.Z ? 1 : e.Z >= e.X ? 2 : 0;
            Array.Sort(_order, start, count, new TriComparer(_centroids, axis));
            int mid = count / 2;
            int left = Build(start, mid);
            int right = Build(start + mid, count - mid);
            _nodes[id] = new Node { Min = mn, Max = mx, Left = left, Right = right };
            return id;
        }

        public bool Hit(Vector3 origin, Vector3 dir, float maxT, out int tri, out float u, out float v, bool frontOnly = false)
        {
            tri = -1; u = v = 0;
            float bestT = maxT;
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int id = stack[--sp];
                ref var node = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes)[id];
                if (!HitBox(node, origin, dir, bestT)) continue;
                if (node.Leaf)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                        HitTri(_order[i], origin, dir, ref bestT, ref tri, ref u, ref v, frontOnly);
                    continue;
                }
                stack[sp++] = node.Left;
                stack[sp++] = node.Right;
            }
            return tri >= 0;
        }

        private void HitTri(int t, Vector3 o, Vector3 d, ref float bestT, ref int bestTri, ref float bestU, ref float bestV, bool frontOnly)
        {
            var p0 = _pos[_tris[t * 3]];
            var p1 = _pos[_tris[t * 3 + 1]];
            var p2 = _pos[_tris[t * 3 + 2]];
            var e1 = p1 - p0;
            var e2 = p2 - p0;
            if (frontOnly && Vector3.Dot(Vector3.Cross(e1, e2), d) <= 0) return;
            var h = Vector3.Cross(d, e2);
            float a = Vector3.Dot(e1, h);
            if (MathF.Abs(a) < 1e-8f) return;
            float f = 1 / a;
            var s = o - p0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1) return;
            var q = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(d, q);
            if (v < 0 || u + v > 1) return;
            float dist = f * Vector3.Dot(e2, q);
            if (dist <= 1e-4f || dist >= bestT) return;
            bestT = dist; bestTri = t; bestU = u; bestV = v;
        }

        private static bool HitBox(in Node node, Vector3 o, Vector3 d, float maxT)
        {
            float t0 = 0, t1 = maxT;
            if (!Slab(o.X, d.X, node.Min.X, node.Max.X, ref t0, ref t1)) return false;
            if (!Slab(o.Y, d.Y, node.Min.Y, node.Max.Y, ref t0, ref t1)) return false;
            if (!Slab(o.Z, d.Z, node.Min.Z, node.Max.Z, ref t0, ref t1)) return false;
            return t0 <= t1;
        }

        private static bool Slab(float o, float d, float mn, float mx, ref float t0, ref float t1)
        {
            if (MathF.Abs(d) < 1e-9f) return o >= mn && o <= mx;
            float inv = 1 / d;
            float a = (mn - o) * inv, b = (mx - o) * inv;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            return t0 <= t1;
        }

        private sealed class TriComparer : IComparer<int>
        {
            private readonly Vector3[] _c;
            private readonly int _axis;
            public TriComparer(Vector3[] c, int axis) { _c = c; _axis = axis; }
            public int Compare(int a, int b) => _c[a][_axis].CompareTo(_c[b][_axis]);
        }
    }
}
