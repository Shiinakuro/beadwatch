using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;

namespace Doudou.Core.Recon;

/// <summary>
/// 多视图投影上色：每颗表面豆，从「能直接看到它」的视图里取色，按朝向加权平均。
/// 看不到的豆（凹陷处、内部）从最近的已上色邻居扩散继承。
/// 结果是连续色（Lab），之后交给 <see cref="Quantizer"/> 映射到品牌色号。
/// </summary>
public static class ColorProjector
{
    public sealed class Result
    {
        public Lab?[] Colors = Array.Empty<Lab?>();
        /// <summary>是否至少被一张视图直接看到（false = 继承来的颜色）。</summary>
        public bool[] Seen = Array.Empty<bool>();
        public int SeenVoxels;
        public int InheritedVoxels;
        public Dictionary<ViewKind, double> Coverage = new();
    }

    public static Result Project(Occupancy occ, IReadOnlyList<ViewImage> views)
    {
        int n = occ.Cells.Length;
        double[] sumL = new double[n], sumA = new double[n], sumB = new double[n], wsum = new double[n];
        var res = new Result();

        var normals = EstimateNormals(occ);
        foreach (var v in views)
        {
            if (v.ShapeOnly) continue;
            var vdir = ViewGeometry.Dir(v.Kind);
            var src = v.ColorSource ?? v;
            var map = ViewMapping.For(occ, src);
            var (axis, sign) = ViewGeometry.AxisSign(v.Kind);
            int seen = 0;
            // 沿视线方向逐列找第一颗豆
            int na = axis == 0 ? occ.SY : occ.SX, nb = axis == 2 ? occ.SY : occ.SZ, nc = axis == 0 ? occ.SX : axis == 1 ? occ.SY : occ.SZ;
            for (int ib = 0; ib < nb; ib++)
                for (int ia = 0; ia < na; ia++)
                {
                    for (int step = 0; step < nc; step++)
                    {
                        int ic = sign > 0 ? nc - 1 - step : step;
                        var p = axis switch
                        {
                            0 => new Int3(ic, ia, ib),
                            1 => new Int3(ia, ic, ib),
                            _ => new Int3(ia, ib, ic),
                        };
                        if (!occ[p.X, p.Y, p.Z]) continue;
                        // 第一颗：采样该豆朝向相机那一面的投影区域
                        var face = new Vector3(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.5f) + ViewGeometry.Dir(v.Kind) * 0.5f;
                        var lab = Sample(src, map, face);
                        if (lab.HasValue)
                        {
                            int i = occ.Index(p.X, p.Y, p.Z);
                            // 只让朝向与视线大致对齐的视图参与：背向/垂直于表面的正面图不参与，
                            // 否则前层头发/脸的颜色会漏到后面被遮挡的表面上。
                            double cos = Vector3.Dot(normals[i], vdir);
                            if (cos < 0.12) break;
                            double w = cos * cos * cos * (v.Kind == ViewKind.Front ? 1.3 : 1.0);
                            sumL[i] += lab.Value.L * w; sumA[i] += lab.Value.A * w; sumB[i] += lab.Value.B * w; wsum[i] += w;
                            seen++;
                        }
                        break;
                    }
                }
            res.Coverage[v.Kind] = seen;
        }

        // 保边平滑：表面上相邻且色差小的取色互相平均，去掉渲染高光/渐变造成的颗粒感，色块边界保留
        for (int it = 0; it < 3; it++)
        {
            var nl = (double[])sumL.Clone(); var na = (double[])sumA.Clone(); var nb2 = (double[])sumB.Clone(); var nw = (double[])wsum.Clone();
            for (int i = 0; i < n; i++)
            {
                if (wsum[i] <= 0) continue;
                var me = new Lab(sumL[i] / wsum[i], sumA[i] / wsum[i], sumB[i] / wsum[i]);
                int x = i % occ.SX, t2 = i / occ.SX, y = t2 % occ.SY, z = t2 / occ.SY;
                double aL = me.L, aA = me.A, aB = me.B, aw = 1;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if ((dx | dy | dz) == 0 || !occ.In(x + dx, y + dy, z + dz)) continue;
                            int j = occ.Index(x + dx, y + dy, z + dz);
                            if (wsum[j] <= 0) continue;
                            var o = new Lab(sumL[j] / wsum[j], sumA[j] / wsum[j], sumB[j] / wsum[j]);
                            double de = ColorMath.DeltaE2000(me, o);
                            if (de > 9) continue;
                            double w = 1 - de / 9;
                            aL += o.L * w; aA += o.A * w; aB += o.B * w; aw += w;
                        }
                nl[i] = aL / aw; na[i] = aA / aw; nb2[i] = aB / aw; nw[i] = 1;
            }
            sumL = nl; sumA = na; sumB = nb2; wsum = nw;
        }

        var colors = new Lab?[n];
        res.Seen = new bool[n];
        // 表面层：至少有一个 6 邻域是空的/边界。继承只在表面扩散，不能穿过内部把前层颜色带到后层。
        var surface = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!occ.Cells[i]) continue;
            int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
            foreach (var d in Dir6.All)
                if (!occ[x + d.X, y + d.Y, z + d.Z]) { surface[i] = true; break; }
        }

        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (!occ.Cells[i] || !surface[i] || wsum[i] <= 0) continue;
            colors[i] = new Lab(sumL[i] / wsum[i], sumA[i] / wsum[i], sumB[i] / wsum[i]);
            res.Seen[i] = true;
            queue.Enqueue(i);
            res.SeenVoxels++;
        }
        // 表面内扩散：看不见的表面从最近的已取色表面继承，不经过内部。
        while (queue.Count > 0)
        {
            int j = queue.Dequeue();
            int x = j % occ.SX, t = j / occ.SX, y = t % occ.SY, z = t / occ.SY;
            foreach (var d in Dir6.All)
            {
                int ax = x + d.X, ay = y + d.Y, az = z + d.Z;
                if (!occ.In(ax, ay, az)) continue;
                int k = occ.Index(ax, ay, az);
                if (!occ.Cells[k] || !surface[k] || colors[k].HasValue) continue;
                colors[k] = colors[j];
                res.InheritedVoxels++;
                queue.Enqueue(k);
            }
        }
        // 内部豆只用于填补模型内部，不反过来影响表面颜色。
        var fill = new Queue<int>();
        for (int i = 0; i < n; i++)
            if (occ.Cells[i] && colors[i].HasValue) fill.Enqueue(i);
        while (fill.Count > 0)
        {
            int j = fill.Dequeue();
            int x = j % occ.SX, t = j / occ.SX, y = t % occ.SY, z = t / occ.SY;
            foreach (var d in Dir6.All)
            {
                int ax = x + d.X, ay = y + d.Y, az = z + d.Z;
                if (!occ.In(ax, ay, az)) continue;
                int k = occ.Index(ax, ay, az);
                if (!occ.Cells[k] || surface[k] || colors[k].HasValue) continue;
                colors[k] = colors[j];
                fill.Enqueue(k);
            }
        }
        // 极少数与已取色表面断开的表面块：退回已取色表面的平均色，避免留下空色。
        double fl = 0, fa = 0, fb = 0; int fc = 0;
        for (int i = 0; i < n; i++)
            if (res.Seen[i] && colors[i] is { } c) { fl += c.L; fa += c.A; fb += c.B; fc++; }
        if (fc > 0)
        {
            var fallback = new Lab(fl / fc, fa / fc, fb / fc);
            for (int i = 0; i < n; i++)
                if (occ.Cells[i] && surface[i] && !colors[i].HasValue)
                {
                    colors[i] = fallback;
                    res.InheritedVoxels++;
                }
        }
        res.Colors = colors;
        return res;
    }

    /// <summary>用 5×5×5 邻域的占位梯度估计每颗豆的表面法线（指向外侧）。</summary>
    public static Vector3[] EstimateNormals(Occupancy occ, int r = 2)
    {
        var n = new Vector3[occ.Cells.Length];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    var g = Vector3.Zero;
                    for (int dz = -r; dz <= r; dz++)
                        for (int dy = -r; dy <= r; dy++)
                            for (int dx = -r; dx <= r; dx++)
                                if (occ[x + dx, y + dy, z + dz]) g += new Vector3(dx, dy, dz);
                    n[i] = g.LengthSquared() < 1e-6 ? Vector3.UnitZ : Vector3.Normalize(-g);
                }
        return n;
    }

    /// <summary>
    /// 细节保护取色：把方块内像素分成两簇（2-means）。
    /// 若少数簇明显更深（眼睛、嘴、描边这类细线）且占比不太小，就取它——否则细线会被平均成一片浑色；
    /// 其余情况取多数簇的均值（避免两种颜色混成第三种颜色）。
    /// </summary>
    public static Lab Dominant(List<Lab> px)
    {
        if (px.Count <= 2) return Mean(px);
        var a = px.MinBy(p => p.L); var b = px.MaxBy(p => p.L);
        var ca = new List<Lab>(); var cb = new List<Lab>();
        for (int it = 0; it < 4; it++)
        {
            ca.Clear(); cb.Clear();
            foreach (var p in px) (D(p, a) <= D(p, b) ? ca : cb).Add(p);
            if (ca.Count == 0 || cb.Count == 0) return Mean(px);
            a = Mean(ca); b = Mean(cb);
        }
        double sep = ColorMath.DeltaE2000(a, b);
        if (sep < 12) return Mean(px);
        bool aMajor = ca.Count >= cb.Count;
        Lab major = aMajor ? a : b, minor = aMajor ? b : a;
        var minorList = aMajor ? cb : ca;
        double frac = (double)minorList.Count / px.Count;
        bool minorDarker = minor.L < major.L - 15;
        if (minorDarker && frac >= 0.2 && sep >= 20) return minor;
        return major;

        static double D(Lab x, Lab y) { double l = x.L - y.L, aa = x.A - y.A, bb = x.B - y.B; return l * l + aa * aa + bb * bb; }
    }

    private static Lab Mean(List<Lab> px)
    {
        double l = 0, a = 0, b = 0;
        foreach (var p in px) { l += p.L; a += p.A; b += p.B; }
        return new Lab(l / px.Count, a / px.Count, b / px.Count);
    }

    /// <summary>在一个体素面的投影方块里取前景像素的平均色（取中间 70% 区域，避开边缘混色）。</summary>
    private static Lab? Sample(ViewImage v, ViewMapping map, Vector3 faceCenter)
    {
        var (cx, cy) = map.ToImage(faceCenter);
        // 用整格（不重叠的分区）判断是否压到线稿骨架：保证线条连续；颜色则只看格子中间 70%
        double cell = Math.Max(1, map.PixelsPerVoxel);
        int gx0 = (int)Math.Floor(cx - cell / 2), gx1 = (int)Math.Floor(cx + cell / 2 - 1e-6);
        int gy0 = (int)Math.Floor(cy - cell / 2), gy1 = (int)Math.Floor(cy + cell / 2 - 1e-6);
        double half = Math.Max(0.5, cell * 0.35);
        int x0 = (int)Math.Floor(cx - half), x1 = (int)Math.Ceiling(cx + half);
        int y0 = (int)Math.Floor(cy - half), y1 = (int)Math.Ceiling(cy + half);
        var img = v.ColorImage;
        var (dark, skel) = v.Lines;
        int skelHits = 0;
        var darkPx = new List<Lab>();
        for (int y = Math.Max(0, gy0); y <= Math.Min(img.H - 1, gy1); y++)
            for (int x = Math.Max(0, gx0); x <= Math.Min(img.W - 1, gx1); x++)
            {
                int i = y * img.W + x;
                if (skel[i]) skelHits++;
                if (dark[i]) darkPx.Add(ColorMath.ToLab(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]));
            }
        if (skelHits > 0 && darkPx.Count > 0)
        {
            // 线条格：取深色像素的中位亮度那个
            darkPx.Sort((p, q) => p.L.CompareTo(q.L));
            return darkPx[darkPx.Count / 2];
        }
        var px = new List<Lab>();
        for (int y = Math.Max(0, y0); y <= Math.Min(img.H - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(img.W - 1, x1); x++)
            {
                if (!v.Sil.At(x, y)) continue;
                int o = (y * img.W + x) * 4;
                if (dark[y * img.W + x]) continue;   // 非线条格不让细线混色
                px.Add(ColorMath.ToLab(img.Px[o], img.Px[o + 1], img.Px[o + 2]));
            }
        if (px.Count == 0 && darkPx.Count > 0) px = darkPx;
        int cnt = px.Count;
        if (cnt == 0)
        {
            // 轮廓边缘对不上时，退而取最近的前景像素
            int ix = (int)Math.Round(cx), iy = (int)Math.Round(cy);
            for (int rad = 1; rad <= Math.Max(2, (int)(map.PixelsPerVoxel * 1.5)); rad++)
                for (int dy = -rad; dy <= rad; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        if (Math.Abs(dx) != rad && Math.Abs(dy) != rad) continue;
                        int qx = ix + dx, qy = iy + dy;
                        if (!v.Sil.At(qx, qy)) continue;
                        var c = img.GetRgb(qx, qy);
                        return ColorMath.ToLab(c);
                    }
            return null;
        }
        return Majority(px);
    }

    /// <summary>2-means 取多数簇均值：避免两种颜色混成第三种颜色。</summary>
    public static Lab Majority(List<Lab> px)
    {
        if (px.Count <= 2) return Mean(px);
        var a = px.MinBy(p => p.L); var b = px.MaxBy(p => p.L);
        var ca = new List<Lab>(); var cb = new List<Lab>();
        for (int it = 0; it < 4; it++)
        {
            ca.Clear(); cb.Clear();
            foreach (var p in px) (D(p, a) <= D(p, b) ? ca : cb).Add(p);
            if (ca.Count == 0 || cb.Count == 0) return Mean(px);
            a = Mean(ca); b = Mean(cb);
        }
        return ca.Count >= cb.Count ? a : b;
        static double D(Lab x, Lab y) { double l = x.L - y.L, aa = x.A - y.A, bb = x.B - y.B; return l * l + aa * aa + bb * bb; }
    }
}
