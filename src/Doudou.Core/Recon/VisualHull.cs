using System.Numerics;

namespace Doudou.Core.Recon;

/// <summary>
/// 轮廓雕刻（Visual Hull）：不需要显卡的兜底重建。
/// 每个体素投到每张视图上，只要落在任意一张视图的背景里就被雕掉。
/// 只能还原「凸出来的外形」，凹陷（如两腿之间从正侧都看不到的缝）会被填实——这是方法本身的边界。
/// </summary>
public static class VisualHull
{
    public static Occupancy Carve(IReadOnlyList<ViewImage> views, int targetHeight)
    {
        var front = views.FirstOrDefault(v => v.Kind == ViewKind.Front)
                    ?? views.FirstOrDefault(v => v.Kind == ViewKind.Back)
                    ?? throw new InvalidOperationException("轮廓雕刻至少需要正面或背面视图");
        var side = views.FirstOrDefault(v => v.Kind is ViewKind.Left or ViewKind.Right);
        var top = views.FirstOrDefault(v => v.Kind is ViewKind.Top or ViewKind.Bottom);

        int sy = targetHeight;
        int sx = Math.Max(1, (int)Math.Round(front.Sil.BoxW * (double)sy / front.Sil.BoxH));
        int sz;
        if (side != null) sz = Math.Max(1, (int)Math.Round(side.Sil.BoxW * (double)sy / side.Sil.BoxH));
        else if (top != null) sz = Math.Max(1, (int)Math.Round(top.Sil.BoxH * (double)sx / top.Sil.BoxW));
        else sz = Math.Max(1, (int)Math.Round(sx * 0.6));

        var occ = new Occupancy(sx, sy, sz);
        Array.Fill(occ.Cells, true);
        // 每个视图用「满格包围盒」做映射：先假设网格填满，投影范围就是整个网格
        foreach (var v in views)
        {
            var map = new ViewMapping(v.Kind, sx, sy, sz, v.Sil, ViewMapping.ProjectedRange((_, _, _) => true, sx, sy, sz, v.Kind));
            for (int z = 0; z < sz; z++)
                for (int y = 0; y < sy; y++)
                    for (int x = 0; x < sx; x++)
                    {
                        int i = occ.Index(x, y, z);
                        if (!occ.Cells[i]) continue;
                        // 体素中心附近取 5 个点，过半在前景内才保留，减轻锯齿
                        int inside = 0;
                        for (int k = 0; k < 5; k++)
                        {
                            var off = k switch
                            {
                                0 => new Vector3(0.5f, 0.5f, 0.5f),
                                1 => new Vector3(0.25f, 0.25f, 0.25f),
                                2 => new Vector3(0.75f, 0.25f, 0.75f),
                                3 => new Vector3(0.25f, 0.75f, 0.75f),
                                _ => new Vector3(0.75f, 0.75f, 0.25f),
                            };
                            var (px, py) = map.ToImage(new Vector3(x, y, z) + off);
                            if (v.Sil.At((int)px, (int)py)) inside++;
                        }
                        if (inside < 3) occ.Cells[i] = false;
                    }
        }
        occ.KeepLargestComponent(0.02);
        return occ.Cropped();
    }
}
