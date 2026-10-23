using Doudou.Core.Color;

namespace Doudou.Core.Imaging;

/// <summary>前景掩码 + 包围盒。</summary>
public sealed class Silhouette
{
    public int W { get; }
    public int H { get; }
    public bool[] Mask { get; }
    public int X0 { get; private set; }
    public int Y0 { get; private set; }
    public int X1 { get; private set; }   // 含
    public int Y1 { get; private set; }   // 含

    public int BoxW => X1 - X0 + 1;
    public int BoxH => Y1 - Y0 + 1;
    public bool Empty => X1 < X0;

    public Silhouette(int w, int h, bool[] mask)
    {
        W = w; H = h; Mask = mask;
        UpdateBox();
    }

    public bool At(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H && Mask[y * W + x];

    public void UpdateBox()
    {
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (Mask[y * W + x])
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
    }

    /// <summary>
    /// 从图片得到前景：有透明通道就用透明通道；否则把与边框连通、且接近背景色的像素视为背景（适合白底/纯色底）。
    /// </summary>
    public static Silhouette FromImage(RgbaImage img, double bgTolerance = 10)
    {
        int w = img.W, h = img.H;
        var mask = new bool[w * h];
        int transparent = 0;
        for (int i = 0; i < w * h; i++) if (img.Px[i * 4 + 3] < 250) transparent++;
        int chroma;
        BackgroundRemoval.Checker? checker;
        if (transparent > w * h / 50)
        {
            for (int i = 0; i < w * h; i++) mask[i] = img.Px[i * 4 + 3] >= 128;
        }
        else if ((chroma = BackgroundRemoval.DetectChroma(img)) != 0)
        {
            mask = BackgroundRemoval.ChromaMask(img, chroma);
        }
        else if ((checker = BackgroundRemoval.DetectChecker(img)) != null)
        {
            mask = BackgroundRemoval.CheckerMask(img, checker);
        }
        else
        {
            // 估计背景色：边框像素里出现频率高的颜色（可能不止一种——AI 偶尔把「透明」画成灰白棋盘格）
            var border = new List<Rgb>();
            for (int x = 0; x < w; x += Math.Max(1, w / 256)) { border.Add(img.GetRgb(x, 0)); border.Add(img.GetRgb(x, h - 1)); border.Add(img.GetRgb(x, 1)); border.Add(img.GetRgb(x, h - 2)); }
            for (int y = 0; y < h; y += Math.Max(1, h / 256)) { border.Add(img.GetRgb(0, y)); border.Add(img.GetRgb(w - 1, y)); border.Add(img.GetRgb(1, y)); border.Add(img.GetRgb(w - 2, y)); }
            var bgLabs = new List<Lab>();
            foreach (var g in border.GroupBy(c => (c.R / 12, c.G / 12, c.B / 12)).OrderByDescending(g => g.Count()))
            {
                if (g.Count() < border.Count * 0.08 || bgLabs.Count >= 3) break;
                var list = g.ToList();
                bgLabs.Add(ColorMath.ToLab(new Rgb(Median(list.Select(c => c.R)), Median(list.Select(c => c.G)), Median(list.Select(c => c.B)))));
            }
            if (bgLabs.Count == 0) bgLabs.Add(ColorMath.ToLab(new Rgb(255, 255, 255)));
            var isBg = new bool[w * h];
            var stack = new Stack<int>();
            for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }
            var cache = new Dictionary<int, bool>();
            bool Near(int i)
            {
                int key = (img.Px[i * 4] << 16) | (img.Px[i * 4 + 1] << 8) | img.Px[i * 4 + 2];
                if (cache.TryGetValue(key, out var v)) return v;
                var lab = ColorMath.ToLab(img.Px[i * 4], img.Px[i * 4 + 1], img.Px[i * 4 + 2]);
                v = bgLabs.Any(b => ColorMath.DeltaE2000(lab, b) <= bgTolerance);
                cache[key] = v;
                return v;
            }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (isBg[i] || !Near(i)) continue;
                isBg[i] = true;
                int x = i % w, y = i / w;
                if (x > 0) stack.Push(i - 1);
                if (x < w - 1) stack.Push(i + 1);
                if (y > 0) stack.Push(i - w);
                if (y < h - 1) stack.Push(i + w);
            }
            for (int i = 0; i < w * h; i++) mask[i] = !isBg[i];
        }
        var s = new Silhouette(w, h, mask);
        s.RemoveSpecks();
        return s;
    }

    private static byte Median(IEnumerable<byte> v)
    {
        var a = v.OrderBy(b => b).ToArray();
        return a.Length == 0 ? (byte)255 : a[a.Length / 2];
    }

    /// <summary>只保留面积足够大的连通块（去掉噪点），并填掉内部小洞。</summary>
    public void RemoveSpecks()
    {
        var label = new int[W * H];
        var sizes = new List<int> { 0 };
        var stack = new Stack<int>();
        for (int i = 0; i < W * H; i++)
        {
            if (!Mask[i] || label[i] != 0) continue;
            int id = sizes.Count, n = 0;
            stack.Push(i); label[i] = id;
            while (stack.Count > 0)
            {
                int j = stack.Pop(); n++;
                int x = j % W, y = j / W;
                void Try(int k) { if (Mask[k] && label[k] == 0) { label[k] = id; stack.Push(k); } }
                if (x > 0) Try(j - 1);
                if (x < W - 1) Try(j + 1);
                if (y > 0) Try(j - W);
                if (y < H - 1) Try(j + W);
            }
            sizes.Add(n);
        }
        if (sizes.Count <= 1) { UpdateBox(); return; }
        int largest = sizes.Max();
        for (int i = 0; i < W * H; i++)
            if (Mask[i] && sizes[label[i]] < largest * 0.02) Mask[i] = false;
        UpdateBox();
    }

    /// <summary>把前景外的像素变透明，输出去背景的 RGBA 图（给混元用）。</summary>
    public RgbaImage ApplyTo(RgbaImage img)
    {
        var o = new RgbaImage(img.W, img.H, (byte[])img.Px.Clone());
        for (int i = 0; i < W * H; i++) if (!Mask[i]) o.Px[i * 4 + 3] = 0; else o.Px[i * 4 + 3] = 255;
        return o;
    }
}
