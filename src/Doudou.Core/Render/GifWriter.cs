using SkiaSharp;

namespace Doudou.Core.Render;

/// <summary>
/// 自包含的动画 GIF（GIF89a）编码器：中位切分量化到 256 色全局调色板 + 标准 LZW 压缩 + 无限循环。
/// 不依赖任何第三方库。要求所有帧尺寸一致、不透明。
/// </summary>
public static class GifWriter
{
    public static void Save(IReadOnlyList<SKBitmap> frames, string path, int delayMs = 100, int loop = 0)
    {
        if (frames == null || frames.Count == 0) throw new ArgumentException("没有可写的帧", nameof(frames));
        int w = frames[0].Width, h = frames[0].Height;
        if (w <= 0 || h <= 0) throw new ArgumentException("帧尺寸非法");
        foreach (var f in frames)
            if (f.Width != w || f.Height != h) throw new ArgumentException("所有帧的尺寸必须一致");

        var palette = Quantize(Histogram(frames), 256);   // 全局调色板（≤256，不足补黑）
        var cache = new Dictionary<int, byte>();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write("GIF89a"u8.ToArray());
        bw.Write((ushort)w); bw.Write((ushort)h);
        bw.Write((byte)0xF7);            // 有全局调色板、8bit 色深、大小 256（size code = 7）
        bw.Write((byte)0);               // 背景色索引
        bw.Write((byte)0);               // 像素宽高比
        for (int i = 0; i < 256; i++)
        {
            if (i < palette.Length) { bw.Write(palette[i].Red); bw.Write(palette[i].Green); bw.Write(palette[i].Blue); }
            else { bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); }
        }

        // Netscape 循环扩展
        bw.Write((byte)0x21); bw.Write((byte)0xFF); bw.Write((byte)0x0B);
        bw.Write("NETSCAPE2.0"u8.ToArray());
        bw.Write((byte)0x03); bw.Write((byte)0x01);
        bw.Write((ushort)(loop & 0xFFFF)); bw.Write((byte)0x00);

        int delayCs = Math.Clamp(delayMs / 10, 1, 65535);
        foreach (var f in frames)
        {
            bw.Write((byte)0x21); bw.Write((byte)0xF9); bw.Write((byte)0x04);
            bw.Write((byte)0x04);        // 处置方式=1（不清除）、无用户输入、无透明
            bw.Write((ushort)delayCs);
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((byte)0x2C);        // 图像描述符
            bw.Write((ushort)0); bw.Write((ushort)0);
            bw.Write((ushort)w); bw.Write((ushort)h);
            bw.Write((byte)0);           // 无局部调色板
            WriteLzw(bw, MapToIndices(f, palette, cache), 8);
        }
        bw.Write((byte)0x3B);            // 结束
        bw.Flush();
    }

    private static Dictionary<int, int> Histogram(IReadOnlyList<SKBitmap> frames)
    {
        var hist = new Dictionary<int, int>();
        foreach (var f in frames)
        {
            var span = f.GetPixelSpan();
            int n = f.Width * f.Height;
            for (int p = 0; p < n; p++)
            {
                int o = p * 4;
                int key = (span[o] << 16) | (span[o + 1] << 8) | span[o + 2];
                hist[key] = hist.GetValueOrDefault(key) + 1;
            }
        }
        return hist;
    }

    /// <summary>中位切分：把带权重的颜色集合递归对半切，直到 ≤maxColors 个盒子，各盒取加权平均色。</summary>
    private static SKColor[] Quantize(Dictionary<int, int> hist, int maxColors)
    {
        if (hist.Count == 0) return new[] { SKColors.Black };
        var boxes = new List<List<(int Color, int Count)>> { hist.Select(kv => (kv.Key, kv.Value)).ToList() };
        while (boxes.Count < maxColors)
        {
            int bi = -1; long bestPop = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                long pop = 0; foreach (var x in boxes[i]) pop += x.Count;
                if (pop > bestPop) { bestPop = pop; bi = i; }
            }
            if (bi < 0) break;
            var box = boxes[bi];
            int axis = LongestAxis(box);
            box.Sort((a, b) => Chan(a.Color, axis).CompareTo(Chan(b.Color, axis)));
            long total = 0; foreach (var x in box) total += x.Count;
            long half = total / 2, acc = 0; int cut = box.Count - 2;
            for (int i = 0; i < box.Count; i++) { acc += box[i].Count; if (acc >= half) { cut = i; break; } }
            cut = Math.Clamp(cut, 0, box.Count - 2);
            var left = box.GetRange(0, cut + 1);
            var right = box.GetRange(cut + 1, box.Count - cut - 1);
            boxes.RemoveAt(bi); boxes.Add(left); boxes.Add(right);
        }
        var pal = new SKColor[Math.Min(maxColors, boxes.Count)];
        for (int i = 0; i < pal.Length; i++)
        {
            long r = 0, g = 0, b = 0, cnt = 0;
            foreach (var (color, count) in boxes[i])
            {
                r += (long)((color >> 16) & 0xFF) * count;
                g += (long)((color >> 8) & 0xFF) * count;
                b += (long)(color & 0xFF) * count;
                cnt += count;
            }
            if (cnt == 0) cnt = 1;
            pal[i] = new SKColor((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt));
        }
        return pal;
    }

    private static int Chan(int color, int axis) => axis == 0 ? (color >> 16) & 0xFF : axis == 1 ? (color >> 8) & 0xFF : color & 0xFF;

    private static int LongestAxis(List<(int Color, int Count)> box)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        foreach (var (c, _) in box)
        {
            int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            rMin = Math.Min(rMin, r); rMax = Math.Max(rMax, r);
            gMin = Math.Min(gMin, g); gMax = Math.Max(gMax, g);
            bMin = Math.Min(bMin, b); bMax = Math.Max(bMax, b);
        }
        int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin;
        return dr >= dg && dr >= db ? 0 : dg >= db ? 1 : 2;
    }

    private static byte[] MapToIndices(SKBitmap bmp, SKColor[] pal, Dictionary<int, byte> cache)
    {
        int n = bmp.Width * bmp.Height;
        var idx = new byte[n];
        var span = bmp.GetPixelSpan();
        for (int p = 0; p < n; p++)
        {
            int o = p * 4;
            int key = (span[o] << 16) | (span[o + 1] << 8) | span[o + 2];
            if (!cache.TryGetValue(key, out byte ci)) { ci = Nearest(key, pal); cache[key] = ci; }
            idx[p] = ci;
        }
        return idx;
    }

    private static byte Nearest(int key, SKColor[] pal)
    {
        int r = (key >> 16) & 0xFF, g = (key >> 8) & 0xFF, b = key & 0xFF;
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < pal.Length; i++)
        {
            int dr = pal[i].Red - r, dg = pal[i].Green - g, db = pal[i].Blue - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return (byte)best;
    }

    private static void WriteLzw(BinaryWriter bw, byte[] data, int minCodeSize)
    {
        bw.Write((byte)minCodeSize);
        int clearCode = 1 << minCodeSize, eoi = clearCode + 1;
        int next = eoi + 1, codeSize = minCodeSize + 1;
        var dict = new Dictionary<int, int>();
        var outBytes = new List<byte>(data.Length + (data.Length >> 1) + 16);
        int bitBuf = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuf |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8) { outBytes.Add((byte)(bitBuf & 0xFF)); bitBuf >>= 8; bitCount -= 8; }
        }

        Emit(clearCode);
        if (data.Length > 0)
        {
            int cur = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int b = data[i];
                int key = (cur << 8) | b;
                if (dict.TryGetValue(key, out int existing)) { cur = existing; continue; }
                Emit(cur);
                dict[key] = next++;
                // GIF 解码器建表比编码器慢一个码：宽度要在「刚分配的码号 == 2^codeSize」时才 +1，
                // 否则从第 255 个码起读写位宽错位，整条流解码失败。
                if (next - 1 == (1 << codeSize) && codeSize < 12) codeSize++;
                if (next == 4096)
                {
                    Emit(clearCode);
                    dict.Clear();
                    next = eoi + 1; codeSize = minCodeSize + 1;
                }
                cur = b;
            }
            Emit(cur);
        }
        Emit(eoi);
        if (bitCount > 0) outBytes.Add((byte)(bitBuf & 0xFF));

        int i2 = 0;
        while (i2 < outBytes.Count)
        {
            int len = Math.Min(255, outBytes.Count - i2);
            bw.Write((byte)len);
            for (int k = 0; k < len; k++) bw.Write(outBytes[i2 + k]);
            i2 += len;
        }
        bw.Write((byte)0);   // 块结束符
    }
}
