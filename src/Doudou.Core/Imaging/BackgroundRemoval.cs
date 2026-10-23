using Doudou.Core.Color;

namespace Doudou.Core.Imaging;

/// <summary>
/// 背景识别的几种特殊情况：
///  1. 色键底（品红 #FF00FF / 纯绿）——AI 生成视图时我们主动要求的背景，最可靠；
///  2. 假透明棋盘格——AI 有时把「透明背景」直接画成灰白棋盘格；浅色物体与白格颜色几乎一样，
///     不能按颜色判断，只能按「是否符合棋盘格的空间规律」判断。
/// </summary>
public static class BackgroundRemoval
{
    // ───────────── 色键 ─────────────

    /// <summary>边框大部分像素是高饱和品红或绿色时，返回色键类型（1=品红，2=绿），否则 0。</summary>
    public static int DetectChroma(RgbaImage img)
    {
        int mag = 0, green = 0, n = 0;
        foreach (var (x, y) in Border(img.W, img.H, 4))
        {
            var c = img.GetRgb(x, y);
            n++;
            if (IsMagenta(c)) mag++;
            else if (IsGreen(c)) green++;
        }
        if (mag > n * 0.6) return 1;
        if (green > n * 0.6) return 2;
        return 0;
    }

    private static bool IsMagenta(Rgb c) => c.R - c.G > 90 && c.B - c.G > 90 && Math.Abs(c.R - c.B) < 90;
    private static bool IsGreen(Rgb c) => c.G - c.R > 90 && c.G - c.B > 90;

    public static bool[] ChromaMask(RgbaImage img, int kind)
    {
        int w = img.W, h = img.H;
        var fg = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            var c = new Rgb(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]);
            fg[i] = kind == 1 ? !IsMagenta(c) : !IsGreen(c);
        }
        // 去掉与色键混合的边缘像素（腐蚀 1 像素）
        return Erode(fg, w, h, 1);
    }

    // ───────────── 假透明棋盘格 ─────────────

    /// <summary>棋盘格参数：AI 画出来的格子有噪点、格宽不严格一致，所以只记录格宽的大概值。</summary>
    public sealed class Checker
    {
        public int Size;
    }

    /// <summary>像素类别：0 = 其他，1 = 中性灰格，2 = 中性白格，3 = 两者之间的过渡（也是中性色）。</summary>
    private static byte Classify(byte r, byte g, byte b)
    {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        if (mx - mn > 7) return 0;                 // 有色彩倾向（米白、肤色）→ 不是棋盘格
        int v = (r + g + b) / 3;
        if (v >= 236) return 2;
        if (v >= 145 && v <= 205) return 1;
        if (v > 205 && v < 236) return 3;
        return 0;
    }

    private static byte[] ClassMap(RgbaImage img)
    {
        var c = new byte[img.W * img.H];
        for (int i = 0; i < c.Length; i++) c[i] = Classify(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]);
        return c;
    }

    /// <summary>边框一圈若交替出现中性灰与中性白（各占相当比例），判定为假透明棋盘格，并估计格宽。</summary>
    public static Checker? DetectChecker(RgbaImage img)
    {
        if (img.W < 64 || img.H < 64) return null;
        int gray = 0, white = 0, other = 0;
        foreach (var (x, y) in Border(img.W, img.H, 3))
        {
            int i = (y * img.W + x) * 4;
            switch (Classify(img.Px[i], img.Px[i + 1], img.Px[i + 2]))
            {
                case 1: gray++; break;
                case 2: white++; break;
                case 0: other++; break;
            }
        }
        int n = gray + white + other;
        if (n == 0 || gray < n * 0.2 || white < n * 0.2 || other > n * 0.15) return null;
        // 格宽：沿第 2 行统计灰/白游程长度的中位数
        var runs = new List<int>();
        int prev = -1, len = 0;
        for (int x = 0; x < img.W; x++)
        {
            int i = (2 * img.W + x) * 4;
            int c = Classify(img.Px[i], img.Px[i + 1], img.Px[i + 2]);
            if (c == 3) { len++; continue; }
            if (c == prev) len++;
            else { if (prev is 1 or 2 && len > 1) runs.Add(len); prev = c; len = 1; }
        }
        if (runs.Count < 4) return null;
        runs.Sort();
        return new Checker { Size = Math.Clamp(runs[runs.Count / 2], 3, 64) };
    }

    public static bool[] CheckerMask(RgbaImage img, Checker ch)
    {
        int w = img.W, h = img.H;
        var cls = ClassMap(img);
        // 1) 窗口统计：边长约 2.5 个格子的窗口里，灰格、白格都占可观比例，且几乎没有其他颜色 → 背景种子
        int r = Math.Max(3, (int)(ch.Size * 1.25));
        long[] Integral(Func<byte, bool> f)
        {
            var I = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += f(cls[y * w + x]) ? 1 : 0;
                    I[(y + 1) * (w + 1) + x + 1] = I[y * (w + 1) + x + 1] + row;
                }
            }
            return I;
        }
        var ig = Integral(c => c == 1);
        var iw = Integral(c => c == 2);
        var io = Integral(c => c == 0);
        long Sum(long[] I, int x0, int y0, int x1, int y1) => I[y1 * (w + 1) + x1] - I[y0 * (w + 1) + x1] - I[y1 * (w + 1) + x0] + I[y0 * (w + 1) + x0];
        var seed = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                double area = (x1 - x0) * (y1 - y0);
                double g = Sum(ig, x0, y0, x1, y1) / area, wv = Sum(iw, x0, y0, x1, y1) / area, o = Sum(io, x0, y0, x1, y1) / area;
                seed[y * w + x] = g > 0.18 && wv > 0.18 && o < 0.04;
            }
        // 2) 从边框泛洪（只走种子），再向物体边缘逐像素生长（只走中性灰/白/过渡像素），最多 r 步
        var bg = new bool[w * h];
        var q = new Queue<int>();
        // 所有种子都是背景：挂环、鳍间这类被物体围住的棋盘格也要去掉（物体本身不会同时含大量中性灰与中性白）
        for (int i = 0; i < w * h; i++) if (seed[i]) bg[i] = true;
        for (int step = 0; step < r + 2; step++)
        {
            var frontier = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (bg[i] || cls[i] == 0) continue;
                int x = i % w, y = i / w;
                if ((x > 0 && bg[i - 1]) || (x < w - 1 && bg[i + 1]) || (y > 0 && bg[i - w]) || (y < h - 1 && bg[i + w])) frontier.Add(i);
            }
            if (frontier.Count == 0) break;
            foreach (var i in frontier) bg[i] = true;
        }
        var fg = new bool[w * h];
        for (int i = 0; i < w * h; i++) fg[i] = !bg[i];
        return Erode(fg, w, h, 1);
    }

    // ───────────── 工具 ─────────────

    private static void Flood(Queue<int> q, bool[] mark, int w, int h, Func<int, bool> pass)
    {
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int x = i % w, y = i / w;
            void Try(int j) { if (!mark[j] && pass(j)) { mark[j] = true; q.Enqueue(j); } }
            if (x > 0) Try(i - 1);
            if (x < w - 1) Try(i + 1);
            if (y > 0) Try(i - w);
            if (y < h - 1) Try(i + w);
        }
    }

    public static bool[] Erode(bool[] m, int w, int h, int r)
    {
        var o = (bool[])m.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!m[y * w + x]) continue;
                for (int dy = -r; dy <= r && o[y * w + x]; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || !m[ny * w + nx]) { o[y * w + x] = false; break; }
                    }
            }
        return o;
    }

    private static IEnumerable<(int X, int Y)> Border(int w, int h, int depth)
    {
        int step = Math.Max(1, Math.Min(w, h) / 300);
        for (int d = 0; d < depth; d++)
        {
            for (int x = 0; x < w; x += step) { yield return (x, d); yield return (x, h - 1 - d); }
            for (int y = 0; y < h; y += step) { yield return (d, y); yield return (w - 1 - d, y); }
        }
    }
}
