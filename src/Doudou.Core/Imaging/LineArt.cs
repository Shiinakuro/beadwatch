using Doudou.Core.Color;

namespace Doudou.Core.Imaging;

/// <summary>线稿提取：深色线条 → 单像素骨架（Zhang-Suen 细化），用于把五官、花瓣分隔线变成连续的单颗豆线条。</summary>
public static class LineArt
{
    public static (bool[] Dark, bool[] Skeleton) Extract(RgbaImage img, Silhouette sil, double darkL = 48)
    {
        int w = img.W, h = img.H;
        var dark = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (!sil.Mask[i]) continue;
            var lab = ColorMath.ToLab(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]);
            dark[i] = lab.L < darkL;
        }
        var sk = (bool[])dark.Clone();
        Thin(sk, w, h);
        return (dark, sk);
    }

    private static void Thin(bool[] m, int w, int h)
    {
        bool changed = true;
        var del = new List<int>();
        bool P(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && m[y * w + x];
        while (changed)
        {
            changed = false;
            for (int pass = 0; pass < 2; pass++)
            {
                del.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        if (!m[y * w + x]) continue;
                        bool p2 = P(x, y - 1), p3 = P(x + 1, y - 1), p4 = P(x + 1, y), p5 = P(x + 1, y + 1),
                             p6 = P(x, y + 1), p7 = P(x - 1, y + 1), p8 = P(x - 1, y), p9 = P(x - 1, y - 1);
                        int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;
                        int a = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0)
                              + (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                        if (a != 1) continue;
                        if (pass == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                        del.Add(y * w + x);
                    }
                foreach (var i in del) m[i] = false;
                if (del.Count > 0) changed = true;
            }
        }
    }
}
