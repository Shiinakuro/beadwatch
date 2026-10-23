using Doudou.Core.Color;
using Doudou.Core.Imaging;
using Doudou.Core.Model;

namespace Doudou.Core.Recon;

public sealed class FlatPatternOptions
{
    /// <summary>成品最长边最多多少颗豆（高度按主体比例自动计算）。</summary>
    public int LongSide { get; set; } = 64;
    public string BrandId { get; set; } = "mard221";
    public int MaxColors { get; set; } = 16;
    /// <summary>按主体裁剪并去掉背景；关闭时整张图都进入网格。</summary>
    public bool RemoveBackground { get; set; } = true;
    /// <summary>主体四周留出的豆格数。</summary>
    public int Margin { get; set; }
    public bool Despeckle { get; set; } = true;
    public string Name { get; set; } = "平面拼豆";
}

public sealed class FlatPatternResult
{
    public VoxelModel Model { get; init; } = null!;
    public int LongSide { get; init; }
    public bool BackgroundRemoved { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
}

/// <summary>
/// 单张平面图 → 豆格像素图：按主体轮廓裁剪，逐格取前景平均色，
/// 再交给品牌色卡量化（颜色数、去杂色），得到厚度为 1 的平面模型。
/// </summary>
public static class FlatPattern
{
    public static FlatPatternResult Build(RgbaImage src, PaletteLibrary lib, FlatPatternOptions opt,
        Action<string>? say = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int w = src.W, h = src.H;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("图片是空的");

        bool[] fg;
        int bx0 = 0, by0 = 0, bx1 = w - 1, by1 = h - 1;
        if (opt.RemoveBackground)
        {
            say?.Invoke("识别主体轮廓…");
            var sil = Silhouette.FromImage(src);
            if (sil.Empty) throw new InvalidOperationException("没有识别到主体，请换一张主体更清楚的图片，或关闭「去掉背景」");
            fg = sil.Mask;
            bx0 = sil.X0; by0 = sil.Y0; bx1 = sil.X1; by1 = sil.Y1;
        }
        else
        {
            fg = Enumerable.Repeat(true, w * h).ToArray();
        }

        int boxW = bx1 - bx0 + 1, boxH = by1 - by0 + 1;
        int longSide = Math.Clamp(opt.LongSide, 8, 200);
        int margin = Math.Clamp(opt.Margin, 0, 8);
        int contentLong = Math.Max(4, longSide - margin * 2);
        int cw, ch;
        if (boxW >= boxH)
        {
            cw = contentLong;
            ch = Math.Max(1, (int)Math.Round((double)contentLong * boxH / boxW));
        }
        else
        {
            ch = contentLong;
            cw = Math.Max(1, (int)Math.Round((double)contentLong * boxW / boxH));
        }
        int W = cw + margin * 2, H = ch + margin * 2;
        say?.Invoke($"主体比例 {boxW}×{boxH} → 豆图 {W}×{H}（最长边 {longSide} 颗）…");

        var occ = new Occupancy(W, H, 1);
        var colors = new Lab?[W * H];
        int filled = 0;
        for (int y = 0; y < H; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < W; x++)
            {
                int cx = x - margin, cy = y - margin;
                if (cx < 0 || cy < 0 || cx >= cw || cy >= ch) continue;
                int sx0 = bx0 + (int)((long)cx * boxW / cw);
                int sx1 = bx0 + (int)((long)(cx + 1) * boxW / cw);
                int sy0 = by0 + (int)((long)cy * boxH / ch);
                int sy1 = by0 + (int)((long)(cy + 1) * boxH / ch);
                sx1 = Math.Max(sx0 + 1, Math.Min(w, sx1));
                sy1 = Math.Max(sy0 + 1, Math.Min(h, sy1));

                int stepX = Math.Max(1, (sx1 - sx0) / 20);
                int stepY = Math.Max(1, (sy1 - sy0) / 20);
                int total = 0, n = 0;
                long r = 0, g = 0, b = 0;
                for (int py = sy0; py < sy1; py += stepY)
                    for (int px = sx0; px < sx1; px += stepX)
                    {
                        total++;
                        if (!fg[py * w + px]) continue;
                        n++;
                        var c = src.GetRgb(px, py);
                        r += c.R; g += c.G; b += c.B;
                    }
                if (total == 0 || n == 0) continue;
                // 边缘格只要有约 22% 的前景就保留，避免细线、发梢被吃掉
                if (n < total * 0.22) continue;
                colors[occ.Index(x, y, 0)] = ColorMath.ToLab((byte)(r / n), (byte)(g / n), (byte)(b / n));
                occ[x, y, 0] = true;
                filled++;
            }
        }
        if (filled == 0) throw new InvalidOperationException("主体太小，像素化后没有剩下豆格；请调大最长边或换一张图");

        say?.Invoke($"取色完成：{filled:N0} 格，按 {opt.BrandId} 色卡量化（最多 {opt.MaxColors} 色）…");
        var model = Quantizer.Build(occ, colors, lib, new QuantizeOptions
        {
            BrandId = opt.BrandId,
            MaxColors = Math.Clamp(opt.MaxColors, 2, 64),
            DespeckleDeltaE = opt.Despeckle ? 10 : 0,
            ProtectDarkL = 25,
        }, opt.Name);
        model.CompactPalette();
        say?.Invoke($"像素化完成：{model.SX}×{model.SY} 格，{model.CountSolid():N0} 颗，{model.Palette.Count - 1} 色。");
        return new FlatPatternResult
        {
            Model = model,
            LongSide = longSide,
            BackgroundRemoved = opt.RemoveBackground,
            SourceWidth = w,
            SourceHeight = h,
        };
    }
}
