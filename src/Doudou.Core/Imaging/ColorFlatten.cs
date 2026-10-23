using Doudou.Core.Color;

namespace Doudou.Core.Imaging;

/// <summary>
/// 照片去光影（平涂化）：把实拍照片里同一种材质因光照产生的明暗、反光、颗粒，归并成一块纯色。
///  1. 3×3 中值滤波去颗粒与小高光；
///  2. Lab 空间 k-means（亮度权重降低 → 对明暗不敏感）；
///  3. 合并「色相相同、只是明暗不同」的簇（中性色只按亮度合并，黑/灰/白不会被并到一起）；
///  4. 小连通块并入周围多数色（去掉反光点）；
///  5. 每簇代表色取偏亮的分位（去阴影）。
/// 用户可以指定「主色」：主色是固定的簇中心，不参与合并，最终颜色严格等于指定色。
/// </summary>
public static class ColorFlatten
{
    public sealed class Result
    {
        public RgbaImage Image = null!;
        public List<(Rgb Color, int Pixels)> Colors = new();
    }

    public static Result Flatten(RgbaImage img, Silhouette sil, int k = 24, IReadOnlyList<Rgb>? keyColors = null, bool autoLevels = true)
    {
        int w = img.W, h = img.H, n = w * h;
        if (autoLevels) img = AutoLevels(img, sil);
        // 1) 中值滤波（只在前景内）
        var src = Median3(img, sil);
        var lab = new Lab[n];
        var fgIdx = new List<int>(n / 3);
        for (int i = 0; i < n; i++)
        {
            if (!sil.Mask[i]) continue;
            lab[i] = ColorMath.ToLab(src[i * 3], src[i * 3 + 1], src[i * 3 + 2]);
            fgIdx.Add(i);
        }
        var res = new Result { Image = new RgbaImage(w, h, (byte[])img.Px.Clone()) };
        if (fgIdx.Count == 0) return res;

        // 2) k-means（特征 = L·0.5, a, b）；主色作为固定中心
        double wl = 0.5;
        var pinned = (keyColors ?? Array.Empty<Rgb>()).Select(ColorMath.ToLab).ToList();
        if (pinned.Count > 0) wl = 0.3;   // 主色模式：同一主色的亮部与暗部都归到它
        // 给了主色：只用主色（每个像素归到最接近的主色），结果最干净、颜色数可控
        int kk = pinned.Count > 0 ? pinned.Count : k;
        var rng = new Random(7);
        var sample = fgIdx.Count <= 40000 ? fgIdx : Enumerable.Range(0, 40000).Select(_ => fgIdx[rng.Next(fgIdx.Count)]).ToList();
        var centers = new List<double[]>();
        foreach (var p in pinned) centers.Add(new[] { p.L * wl, p.A, p.B });
        if (centers.Count == 0) { var f = lab[sample[rng.Next(sample.Count)]]; centers.Add(new[] { f.L * wl, f.A, f.B }); }
        var dmin = new double[sample.Count];
        while (centers.Count < kk)
        {
            double total = 0;
            for (int s = 0; s < sample.Count; s++)
            {
                var q = lab[sample[s]];
                double best = double.MaxValue;
                foreach (var c in centers) best = Math.Min(best, D(q, c));
                dmin[s] = best; total += best;
            }
            if (total <= 0) break;
            double r = rng.NextDouble() * total;
            int pick = 0;
            for (; pick < sample.Count - 1 && (r -= dmin[pick]) > 0; pick++) { }
            var pq = lab[sample[pick]];
            centers.Add(new[] { pq.L * wl, pq.A, pq.B });
        }
        int K = centers.Count, P = pinned.Count;
        var lblS = new int[sample.Count];
        for (int it = 0; it < 12; it++)
        {
            var sum = new double[K, 3]; var cnt = new int[K];
            for (int s = 0; s < sample.Count; s++)
            {
                var q = lab[sample[s]];
                int b = Nearest(q, centers);
                lblS[s] = b; cnt[b]++;
                sum[b, 0] += q.L * wl; sum[b, 1] += q.A; sum[b, 2] += q.B;
            }
            for (int c = P; c < K; c++)
                if (cnt[c] > 0) centers[c] = new[] { sum[c, 0] / cnt[c], sum[c, 1] / cnt[c], sum[c, 2] / cnt[c] };
        }
        var label = new int[n];
        Array.Fill(label, -1);
        foreach (var i in fgIdx) label[i] = P > 0 ? NearestKey(lab[i], pinned) : Nearest(lab[i], centers);

        // 3) 合并明暗不同的同一材质（主色不参与）
        var parent = Enumerable.Range(0, K).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        while (true)
        {
            var st = Stats(label, lab, fgIdx, K, Find);
            (double Score, int A, int B)? best = null;
            var ks = st.Keys.OrderByDescending(x => st[x].N).ToList();
            for (int i = 0; i < ks.Count; i++)
                for (int j = i + 1; j < ks.Count; j++)
                {
                    int a = ks[i], b = ks[j];
                    if (a < P || b < P) continue;
                    var (_, l1, a1, b1) = st[a]; var (_, l2, a2, b2) = st[b];
                    double c1 = Math.Sqrt(a1 * a1 + b1 * b1), c2 = Math.Sqrt(a2 * a2 + b2 * b2);
                    double dab = Math.Sqrt((a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2)), dl = Math.Abs(l1 - l2);
                    bool ok;
                    if (Math.Min(c1, c2) > 12)
                    {
                        double ang = Math.Abs(Math.Atan2(b1, a1) - Math.Atan2(b2, a2)) * 180 / Math.PI;
                        ang = Math.Min(ang, 360 - ang);
                        ok = ang < 14 && Math.Abs(c1 - c2) < 14 && dl < 28;
                    }
                    else if (Math.Max(c1, c2) < 8) ok = dl < 12;
                    else ok = dab < 6 && dl < 14;
                    if (!ok) continue;
                    double score = dab + dl * 0.3;
                    if (best == null || score < best.Value.Score) best = (score, a, b);
                }
            if (best == null) break;
            parent[best.Value.B] = best.Value.A;
        }
        foreach (var i in fgIdx) label[i] = Find(label[i]);

        // 4) 小连通块并入周围多数色
        int minArea = Math.Max(10, (int)(fgIdx.Count * 0.0004));
        var seen = new bool[n];
        var stack = new Stack<int>();
        var comp = new List<int>();
        foreach (var start in fgIdx)
        {
            if (seen[start]) continue;
            comp.Clear();
            int lb = label[start];
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop(); comp.Add(i);
                int x = i % w, y = i / w;
                void Try(int j) { if (!seen[j] && label[j] == lb) { seen[j] = true; stack.Push(j); } }
                if (x > 0) Try(i - 1);
                if (x < w - 1) Try(i + 1);
                if (y > 0) Try(i - w);
                if (y < h - 1) Try(i + w);
            }
            if (comp.Count >= minArea || lb < P) continue;   // 主色的小块（如眼睛高光、瞳孔）保留
            var votes = new Dictionary<int, int>();
            foreach (var i in comp)
            {
                int x = i % w, y = i / w;
                foreach (var j in new[] { x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1 })
                    if (j >= 0 && label[j] >= 0 && label[j] != lb) votes[label[j]] = votes.GetValueOrDefault(label[j]) + 1;
            }
            if (votes.Count == 0) continue;
            int to = votes.MaxBy(v => v.Value).Key;
            foreach (var i in comp) label[i] = to;
        }

        // 5) 代表色：主色 = 指定色；其他 = 亮度 65 分位 + a/b 中位数
        var groups = fgIdx.GroupBy(i => label[i]).ToList();
        var repr = new Dictionary<int, Rgb>();
        foreach (var g in groups)
        {
            if (g.Key < P) { repr[g.Key] = keyColors![g.Key]; continue; }
            // 亮度取偏亮分位（去阴影）；色相取 a/b 均值方向；彩度取 75 分位（阴影与反光都会让彩度变低）
            var ls = g.Select(i => lab[i].L).OrderBy(v => v).ToList();
            double ma = g.Average(i => lab[i].A), mb = g.Average(i => lab[i].B);
            double hue = Math.Atan2(mb, ma);
            var chroma = g.Select(i => Math.Sqrt(lab[i].A * lab[i].A + lab[i].B * lab[i].B)).OrderBy(v => v).ToList();
            double C = chroma[(int)(chroma.Count * 0.75)];
            if (Math.Sqrt(ma * ma + mb * mb) < 4) C = Math.Sqrt(ma * ma + mb * mb);   // 中性色（黑/白/灰）不加彩度
            repr[g.Key] = ColorMath.FromLab(new Lab(ls[(int)(ls.Count * 0.65)], C * Math.Cos(hue), C * Math.Sin(hue)));
        }
        foreach (var i in fgIdx)
        {
            var c = repr[label[i]];
            res.Image.Px[i * 4] = c.R; res.Image.Px[i * 4 + 1] = c.G; res.Image.Px[i * 4 + 2] = c.B;
        }
        res.Colors = groups.Select(g => (repr[g.Key], g.Count())).OrderByDescending(t => t.Item2).ToList();
        return res;

        double D(Lab q, double[] c) { double dl = q.L * wl - c[0], da = q.A - c[1], db = q.B - c[2]; return dl * dl + da * da + db * db; }
        int Nearest(Lab q, List<double[]> cs)
        {
            int best = 0; double bd = double.MaxValue;
            for (int c = 0; c < cs.Count; c++) { double d = D(q, cs[c]); if (d < bd) { bd = d; best = c; } }
            return best;
        }
    }

    /// <summary>
    /// 自动曝光与白平衡：手机在室内拍的手办普遍偏暗偏黄。
    /// 每个通道把前景的 99.5 分位拉到 250（放大倍数限制在 1~1.7），同时三个通道一起缩放以免偏色过度。
    /// </summary>
    public static RgbaImage AutoLevels(RgbaImage img, Silhouette sil)
    {
        var hist = new int[3, 256];
        int cnt = 0;
        for (int i = 0; i < img.W * img.H; i++)
        {
            if (!sil.Mask[i]) continue;
            cnt++;
            for (int c = 0; c < 3; c++) hist[c, img.Px[i * 4 + c]]++;
        }
        if (cnt < 100) return img;
        var hi = new double[3];
        for (int c = 0; c < 3; c++)
        {
            int acc = 0, v = 255;
            for (; v > 0; v--) { acc += hist[c, v]; if (acc >= cnt * 0.005) break; }
            hi[c] = Math.Max(1, v);
        }
        double common = Math.Clamp(250 / hi.Max(), 1, 1.7);
        var scale = hi.Select(v => Math.Clamp(250 / v, 1, 1.7)).ToArray();
        // 白平衡只做一半，避免把本来偏暖的主体硬拉成冷色
        for (int c = 0; c < 3; c++) scale[c] = common * Math.Sqrt(scale[c] / common);
        var o = new RgbaImage(img.W, img.H, (byte[])img.Px.Clone());
        for (int i = 0; i < img.W * img.H; i++)
        {
            if (!sil.Mask[i]) continue;
            for (int c = 0; c < 3; c++) o.Px[i * 4 + c] = (byte)Math.Min(255, img.Px[i * 4 + c] * scale[c]);
        }
        return o;
    }

    /// <summary>
    /// 主色归属：有明显彩度的像素（阴影里的粉、暗处的红）只在彩色主色里选，不会被归成黑/白/灰；
    /// 接近中性的像素才参与中性主色的比较。亮度权重很低——明暗不同但同色相的都归到同一主色。
    /// </summary>
    public static int NearestKey(Lab q, IReadOnlyList<Lab> keys)
    {
        double qc = Math.Sqrt(q.A * q.A + q.B * q.B);
        int best = -1; double bd = double.MaxValue;
        for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int k = 0; k < keys.Count; k++)
            {
                var c = keys[k];
                double kc = Math.Sqrt(c.A * c.A + c.B * c.B);
                if (pass == 0 && qc > 10 && kc < 8) continue;   // 第一轮：彩色像素跳过中性主色
                double dl = q.L - c.L, da = q.A - c.A, db = q.B - c.B;
                double d = 0.12 * dl * dl + da * da + db * db;
                if (d < bd) { bd = d; best = k; }
            }
        return Math.Max(0, best);
    }

    private static Dictionary<int, (int N, double L, double A, double B)> Stats(int[] label, Lab[] lab, List<int> fg, int k, Func<int, int> find)
    {
        var acc = new Dictionary<int, (int N, double L, double A, double B)>();
        foreach (var i in fg)
        {
            int c = find(label[i]);
            var v = acc.GetValueOrDefault(c);
            acc[c] = (v.N + 1, v.L + lab[i].L, v.A + lab[i].A, v.B + lab[i].B);
        }
        return acc.ToDictionary(kv => kv.Key, kv => (kv.Value.N, kv.Value.L / kv.Value.N, kv.Value.A / kv.Value.N, kv.Value.B / kv.Value.N));
    }

    private static byte[] Median3(RgbaImage img, Silhouette sil)
    {
        int w = img.W, h = img.H;
        var o = new byte[w * h * 3];
        Span<byte> r = stackalloc byte[9], g = stackalloc byte[9], b = stackalloc byte[9];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!sil.Mask[i]) continue;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || !sil.Mask[ny * w + nx]) continue;
                        int j = (ny * w + nx) * 4;
                        r[n] = img.Px[j]; g[n] = img.Px[j + 1]; b[n] = img.Px[j + 2]; n++;
                    }
                o[i * 3] = Med(r[..n]); o[i * 3 + 1] = Med(g[..n]); o[i * 3 + 2] = Med(b[..n]);
            }
        return o;

        static byte Med(Span<byte> s) { s.Sort(); return s[s.Length / 2]; }
    }

    /// <summary>从若干张图里自动提取主色（用于界面上的「主色」初始值）。</summary>
    public static List<Rgb> ExtractKeyColors(IEnumerable<(RgbaImage Img, Silhouette Sil)> views, int max = 12)
    {
        var all = new List<(Rgb C, int N)>();
        foreach (var (img, sil) in views)
        {
            // 缩小到 400 高再算，几百毫秒内完成
            double s = Math.Min(1, 400.0 / Math.Max(1, img.H));
            var small = s < 1 ? img.Resized(Math.Max(1, (int)(img.W * s)), Math.Max(1, (int)(img.H * s))) : img;
            var ss = s < 1 ? Silhouette.FromImage(small) : sil;
            all.AddRange(Flatten(small, ss, 20).Colors);
        }
        // 跨视图合并相近色
        var merged = new List<(Rgb C, int N)>();
        foreach (var (c, n) in all.OrderByDescending(t => t.N))
        {
            int hit = merged.FindIndex(m => ColorMath.DeltaE(m.C, c) < 8);
            if (hit >= 0) merged[hit] = (merged[hit].C, merged[hit].N + n);
            else merged.Add((c, n));
        }
        int total = merged.Sum(m => m.N);
        return merged.Where(m => m.N >= total * 0.002).OrderByDescending(m => m.N).Take(max).Select(m => m.C).ToList();
    }
}
