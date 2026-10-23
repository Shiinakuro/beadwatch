using System.Numerics;
using Doudou.Core.Imaging;

namespace Doudou.Core.Recon;

/// <summary>
/// 体素网格与某张视图之间的正交映射。
/// 把网格在该视图下的投影包围盒，按「高度对齐 + 水平居中」贴到图片前景包围盒上。
/// </summary>
public sealed class ViewMapping
{
    public ViewKind Kind { get; }
    public int SX { get; }
    public int SY { get; }
    public int SZ { get; }

    private readonly Vector3 _r, _u;
    private readonly double _sMin, _sMax, _tMin, _tMax;
    private readonly double _scale, _cx, _cy;

    public double PixelsPerVoxel => _scale;

    public ViewMapping(ViewKind kind, int sx, int sy, int sz, Silhouette sil, (double sMin, double sMax, double tMin, double tMax) proj)
    {
        Kind = kind; SX = sx; SY = sy; SZ = sz;
        _r = ViewGeometry.Right(kind); _u = ViewGeometry.Up(kind);
        (_sMin, _sMax, _tMin, _tMax) = proj;
        double gw = Math.Max(1e-6, _sMax - _sMin), gh = Math.Max(1e-6, _tMax - _tMin);
        // 以高度为准；若宽高比差别很大（视图本身不一致），取两者几何平均以免一侧溢出
        double kh = sil.BoxH / gh, kw = sil.BoxW / gw;
        _scale = Math.Abs(Math.Log(kh / kw)) < 0.25 ? kh : Math.Sqrt(kh * kw);
        _cx = (sil.X0 + sil.X1 + 1) / 2.0;
        _cy = (sil.Y0 + sil.Y1 + 1) / 2.0;
    }

    /// <summary>模型空间的点（体素单位）→ 图像像素坐标。</summary>
    public (double Px, double Py) ToImage(Vector3 p)
    {
        double s = Vector3.Dot(p, _r), t = Vector3.Dot(p, _u);
        double px = _cx + (s - (_sMin + _sMax) / 2) * _scale;
        double py = _cy - (t - (_tMin + _tMax) / 2) * _scale;
        return (px, py);
    }

    /// <summary>计算占位网格在某视图下的投影范围（以体素角点计）。</summary>
    public static (double, double, double, double) ProjectedRange(Func<int, int, int, bool> solid, int sx, int sy, int sz, ViewKind kind)
    {
        var r = ViewGeometry.Right(kind); var u = ViewGeometry.Up(kind);
        double s0 = double.MaxValue, s1 = double.MinValue, t0 = double.MaxValue, t1 = double.MinValue;
        for (int z = 0; z < sz; z++)
            for (int y = 0; y < sy; y++)
                for (int x = 0; x < sx; x++)
                {
                    if (!solid(x, y, z)) continue;
                    var c = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                    double s = Vector3.Dot(c, r), t = Vector3.Dot(c, u);
                    s0 = Math.Min(s0, s - 0.5); s1 = Math.Max(s1, s + 0.5);
                    t0 = Math.Min(t0, t - 0.5); t1 = Math.Max(t1, t + 0.5);
                }
        return (s0, s1, t0, t1);
    }

    public static ViewMapping For(Occupancy occ, ViewImage view) =>
        new(view.Kind, occ.SX, occ.SY, occ.SZ, view.Sil, ProjectedRange((x, y, z) => occ[x, y, z], occ.SX, occ.SY, occ.SZ, view.Kind));
}

/// <summary>轮廓吻合度：网格投影与图片前景的 IoU。</summary>
public static class SilhouetteFit
{
    public static double IoU(Occupancy occ, ViewImage view)
    {
        var map = ViewMapping.For(occ, view);
        // 在网格投影平面上按体素格采样：格内是否有豆 vs 图像该点是否前景
        var r = ViewGeometry.Right(view.Kind); var u = ViewGeometry.Up(view.Kind);
        int inter = 0, uni = 0;
        // 投影平面的格子：用三维循环把每个体素投到 (s,t) 整格
        var proj = new HashSet<(int, int)>();
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                    if (occ[x, y, z])
                    {
                        var c = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                        proj.Add(((int)MathF.Floor(Vector3.Dot(c, r)), (int)MathF.Floor(Vector3.Dot(c, u))));
                    }
        if (proj.Count == 0) return 0;
        int s0 = proj.Min(p => p.Item1) - 2, s1 = proj.Max(p => p.Item1) + 2;
        int t0 = proj.Min(p => p.Item2) - 2, t1 = proj.Max(p => p.Item2) + 2;
        for (int t = t0; t <= t1; t++)
            for (int s = s0; s <= s1; s++)
            {
                // 用该格中心反求一个模型点（深度无关）
                var p = r * (s + 0.5f) + u * (t + 0.5f);
                var (px, py) = map.ToImage(p);
                bool img = view.Sil.At((int)px, (int)py);
                bool grid = proj.Contains((s, t));
                if (img && grid) inter++;
                if (img || grid) uni++;
            }
        return uni == 0 ? 0 : (double)inter / uni;
    }
}
