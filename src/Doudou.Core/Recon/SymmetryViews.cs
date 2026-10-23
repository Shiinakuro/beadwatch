using Doudou.Core.Imaging;

namespace Doudou.Core.Recon;

/// <summary>
/// 近似回转对称的物体（铃铛、花瓶、罐子、圆头公仔）：只有正面图时，用正面轮廓推断侧面/背面「形状图」，
/// 供混元3D 约束形状（不参与取色）。顶部窄小的部分（挂环、天线等）视作薄片，侧面只保留 35% 宽。
/// </summary>
public static class SymmetryViews
{
    public static Dictionary<ViewKind, string> Infer(ViewImage front, string workDir, bool thinTop = true)
    {
        var sil = front.Sil;
        int w = sil.W, h = sil.H;
        var widths = new int[h];
        int maxW = 0;
        for (int y = 0; y < h; y++)
        {
            int x0 = -1, x1 = -1;
            for (int x = 0; x < w; x++) if (sil.Mask[y * w + x]) { if (x0 < 0) x0 = x; x1 = x; }
            widths[y] = x0 < 0 ? 0 : x1 - x0 + 1;
            maxW = Math.Max(maxW, widths[y]);
        }
        int bodyStart = 0;
        for (int y = 0; y < h; y++) if (widths[y] > 0.45 * maxW) { bodyStart = y; break; }
        int margin = (int)(0.06 * sil.BoxH);
        int cx = (sil.X0 + sil.X1) / 2;

        // 主体色：主体区域的中位色（形状图只需要一个干净的单色）
        var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
        for (int y = bodyStart; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (sil.Mask[y * w + x]) { var c = front.Image.GetRgb(x, y); rs.Add(c.R); gs.Add(c.G); bs.Add(c.B); }
        byte Med(List<byte> l) { if (l.Count == 0) return 220; l.Sort(); return l[l.Count / 2]; }
        byte R = Med(rs), G = Med(gs), B = Med(bs);

        var side = new RgbaImage(w, h);
        var back = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            int x0 = -1, x1 = -1;
            for (int x = 0; x < w; x++) if (sil.Mask[y * w + x]) { if (x0 < 0) x0 = x; x1 = x; }
            if (x0 < 0) continue;
            int half = thinTop && y < bodyStart - margin ? Math.Max(4, (int)((x1 - x0) * 0.35 / 2)) : (x1 - x0) / 2;
            for (int x = Math.Max(0, cx - half); x <= Math.Min(w - 1, cx + half); x++) Put(side, x, y);
            for (int x = 0; x < w; x++) if (sil.Mask[y * w + x]) Put(back, w - 1 - x, y);
        }
        void Put(RgbaImage img, int x, int y)
        {
            int o = (y * img.W + x) * 4;
            img.Px[o] = R; img.Px[o + 1] = G; img.Px[o + 2] = B; img.Px[o + 3] = 255;
        }
        Directory.CreateDirectory(workDir);
        var res = new Dictionary<ViewKind, string>();
        foreach (var (k, img) in new[] { (ViewKind.Left, side), (ViewKind.Right, side), (ViewKind.Back, back) })
        {
            var p = Path.Combine(workDir, $"shape_{k.ToString().ToLowerInvariant()}.png");
            img.SavePng(p);
            res[k] = p;
        }
        return res;
    }
}
