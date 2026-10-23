using Doudou.Core.Model;

namespace Doudou.Core.Color;

/// <summary>
/// 去掉表面上零散的错误取色块（例如脸/蝴蝶结的颜色被投到相邻头发/衣服上）。
/// 只在表面做邻域多数色替换：大面积的脸、头发、衣服会保留，深色五官不动。
/// </summary>
public static class SurfaceColorCleaner
{
    public static int Clean(VoxelModel m, int radius = 0, bool protectDark = true)
    {
        int n = m.Colors.Length;
        if (n == 0) return 0;
        int r = radius > 0 ? radius : Math.Clamp(m.SY / 25, 2, 5);
        int minBest = Math.Max(6, r * r * 2);

        var surface = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (m.Colors[i] == 0) continue;
            int x = i % m.SX, t = i / m.SX, y = t % m.SY, z = t / m.SY;
            for (int dz = -1; dz <= 1 && !surface[i]; dz++)
                for (int dy = -1; dy <= 1 && !surface[i]; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) == 1
                            && (!m.InBounds(x + dx, y + dy, z + dz) || m.Colors[m.Index(x + dx, y + dy, z + dz)] == 0))
                        {
                            surface[i] = true;
                            break;
                        }
        }

        var src = (ushort[])m.Colors.Clone();
        var counts = new Dictionary<ushort, int>();
        int changed = 0;
        for (int i = 0; i < n; i++)
        {
            if (!surface[i] || src[i] == 0) continue;
            if (protectDark && m.RgbOf(src[i]).Luma < 55) continue;
            int x = i % m.SX, t = i / m.SX, y = t % m.SY, z = t / m.SY;
            counts.Clear();
            int total = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if ((dx | dy | dz) == 0) continue;
                        int ax = x + dx, ay = y + dy, az = z + dz;
                        if (!m.InBounds(ax, ay, az)) continue;
                        int j = m.Index(ax, ay, az);
                        ushort c = src[j];
                        if (c == 0 || !surface[j]) continue;
                        counts[c] = counts.GetValueOrDefault(c) + 1;
                        total++;
                    }
            if (total < minBest) continue;
            var best = counts.OrderByDescending(kv => kv.Value).First();
            if (best.Key == src[i]) continue;
            if (best.Value < minBest || best.Value < total * 0.5) continue;
            m.Colors[i] = best.Key;
            changed++;
        }
        return changed;
    }
}
