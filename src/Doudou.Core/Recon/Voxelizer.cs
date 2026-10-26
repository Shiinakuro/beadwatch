using System.Numerics;
using Doudou.Core.Geometry;
using Doudou.Core.IO;

namespace Doudou.Core.Recon;

/// <summary>
/// 三角网格 → 实心体素。以体素中心是否在网格内部为准：
/// 沿 X / Y / Z 三个方向各做一遍「射线奇偶」，取多数票，能容忍网格上少量破洞。
/// </summary>
public static class Voxelizer
{
    /// <summary>体素网格与原网格之间的坐标变换。体素坐标以格中心 +0.5 计。</summary>
    public readonly record struct VoxelFrame(Vector3 MeshMin, float Scale, Int3 CropOffset)
    {
        public Vector3 ToVoxel(Vector3 meshPoint) =>
            (meshPoint - MeshMin) * Scale - new Vector3(CropOffset.X, CropOffset.Y, CropOffset.Z);

        public Vector3 ToMesh(Vector3 voxelPoint) =>
            MeshMin + (voxelPoint + new Vector3(CropOffset.X, CropOffset.Y, CropOffset.Z)) / Scale;
    }

    /// <param name="mesh">网格，Y 朝上</param>
    /// <param name="targetHeight">目标高度（豆数 / 层数）</param>
    public static Occupancy Voxelize(TriMesh mesh, int targetHeight) => Voxelize(mesh, targetHeight, out _);

    /// <param name="frame">网格坐标 → 体素坐标的变换，供纹理采样复用。</param>
    public static Occupancy Voxelize(TriMesh mesh, int targetHeight, out VoxelFrame frame)
    {
        var (mn, mx) = mesh.Bounds();
        float h = mx.Y - mn.Y;
        if (h <= 0) throw new InvalidOperationException("网格高度为 0");
        float s = targetHeight / h;
        // 网格坐标 → 体素坐标（每格 1 单位）
        var verts = mesh.Vertices.Select(v => (v - mn) * s).ToArray();
        int sx = Math.Max(1, (int)Math.Ceiling((mx.X - mn.X) * s));
        int sy = Math.Max(1, targetHeight);
        int sz = Math.Max(1, (int)Math.Ceiling((mx.Z - mn.Z) * s));

        var votes = new byte[sx * sy * sz];
        var dims = new[] { sx, sy, sz };
        for (int rayAxis = 0; rayAxis < 3; rayAxis++)
            CastAlong(verts, mesh.Indices, dims, rayAxis, votes);

        var occ = new Occupancy(sx, sy, sz);
        for (int i = 0; i < votes.Length; i++) occ.Cells[i] = votes[i] >= 2;
        occ.KeepLargestComponent(0.002);
        occ.FillCavities();
        frame = new VoxelFrame(mn, s, Int3.Zero);
        var bounds = occ.Bounds();
        var cropped = occ.Cropped();
        if (bounds is { } b) frame = new VoxelFrame(mn, s, b.Min);
        return cropped;
    }

    private static void CastAlong(Vector3[] v, List<int> idx, int[] dims, int c, byte[] votes)
    {
        int a = (c + 1) % 3, b = (c + 2) % 3;
        int na = dims[a], nb = dims[b], nc = dims[c];
        var hits = new List<float>?[na * nb];
        static float G(Vector3 p, int k) => k == 0 ? p.X : k == 1 ? p.Y : p.Z;

        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            var p0 = v[idx[t]]; var p1 = v[idx[t + 1]]; var p2 = v[idx[t + 2]];
            float a0 = G(p0, a), b0 = G(p0, b), a1 = G(p1, a), b1 = G(p1, b), a2 = G(p2, a), b2 = G(p2, b);
            float area = (a1 - a0) * (b2 - b0) - (a2 - a0) * (b1 - b0);
            if (MathF.Abs(area) < 1e-12f) continue;
            int ia0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a0, MathF.Min(a1, a2)) - 0.5f));
            int ia1 = Math.Min(na - 1, (int)MathF.Ceiling(MathF.Max(a0, MathF.Max(a1, a2)) - 0.5f));
            int ib0 = Math.Max(0, (int)MathF.Floor(MathF.Min(b0, MathF.Min(b1, b2)) - 0.5f));
            int ib1 = Math.Min(nb - 1, (int)MathF.Ceiling(MathF.Max(b0, MathF.Max(b1, b2)) - 0.5f));
            for (int ib = ib0; ib <= ib1; ib++)
                for (int ia = ia0; ia <= ia1; ia++)
                {
                    float pa = ia + 0.5f, pb = ib + 0.5f;
                    float w0 = (a1 - pa) * (b2 - pb) - (a2 - pa) * (b1 - pb);
                    float w1 = (a2 - pa) * (b0 - pb) - (a0 - pa) * (b2 - pb);
                    float w2 = (a0 - pa) * (b1 - pb) - (a1 - pa) * (b0 - pb);
                    bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (!inside) continue;
                    // 边界上的点会被两个三角形重复计数——用半开规则粗略去重：恰好为 0 的一律剔掉一侧
                    if (w0 == 0 || w1 == 0 || w2 == 0) { if (((ia + ib) & 1) == 0) continue; }
                    float depth = (w0 * G(p0, c) + w1 * G(p1, c) + w2 * G(p2, c)) / area;
                    (hits[ib * na + ia] ??= new List<float>()).Add(depth);
                }
        }

        for (int ib = 0; ib < nb; ib++)
            for (int ia = 0; ia < na; ia++)
            {
                var list = hits[ib * na + ia];
                if (list == null || list.Count < 2) continue;
                list.Sort();
                // 合并几乎重合的交点（共享边造成的重复）
                var clean = new List<float>(list.Count);
                foreach (var z in list) if (clean.Count == 0 || z - clean[^1] > 1e-4f) clean.Add(z);
                for (int k = 0; k + 1 < clean.Count; k += 2)
                {
                    int ic0 = Math.Max(0, (int)MathF.Ceiling(clean[k] - 0.5f));
                    int ic1 = Math.Min(nc - 1, (int)MathF.Floor(clean[k + 1] - 0.5f));
                    for (int ic = ic0; ic <= ic1; ic++)
                    {
                        int x = c == 0 ? ic : a == 0 ? ia : ib;
                        int y = c == 1 ? ic : a == 1 ? ia : ib;
                        int z = c == 2 ? ic : a == 2 ? ia : ib;
                        votes[(z * dims[1] + y) * dims[0] + x]++;
                    }
                }
            }
    }
}
