using Doudou.Core.Color;

namespace Doudou.Core.Imaging;

/// <summary>
/// 去描边：卡通图的外轮廓粗黑线会被取色采到，导致模型表面一圈脏色。
/// 把「靠近轮廓边缘的深色像素」替换为离它最近的非深色前景像素颜色；内部的五官线条保留。
/// </summary>
public static class OutlineCleaner
{
    public static RgbaImage Clean(RgbaImage img, Silhouette sil, double darkL = 42, int band = 0)
    {
        int w = img.W, h = img.H;
        if (band <= 0) band = Math.Max(3, (int)(Math.Max(sil.BoxW, sil.BoxH) * 0.012));
        // 到背景的距离（棋盘距离，限 band）
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < w * h; i++) if (!sil.Mask[i]) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            if (dist[i] >= band) continue;
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    int j = ny * w + nx;
                    if (dist[j] > dist[i] + 1) { dist[j] = dist[i] + 1; q.Enqueue(j); }
                }
        }
        var dark = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (!sil.Mask[i] || dist[i] > band) continue;
            var lab = ColorMath.ToLab(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]);
            if (lab.L < darkL) dark[i] = true;
        }
        // 从非深色前景像素向深色描边像素扩散颜色
        var o = new RgbaImage(w, h, (byte[])img.Px.Clone());
        var done = new bool[w * h];
        for (int i = 0; i < w * h; i++) if (sil.Mask[i] && !dark[i]) { done[i] = true; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int x = i % w, y = i / w;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int j = ny * w + nx;
                if (done[j] || !dark[j]) continue;
                done[j] = true;
                Buffer.BlockCopy(o.Px, i * 4, o.Px, j * 4, 3);
                q.Enqueue(j);
            }
        }
        return o;
    }
}
