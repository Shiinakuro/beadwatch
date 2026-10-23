using System.Text;
using Doudou.Core.Color;
using Doudou.Core.Model;
using SkiaSharp;

namespace Doudou.Core.Drawing;

public sealed class FlatPatternRenderOptions
{
    public int MaxWidth { get; set; } = 4200;
    public int MinCell { get; set; } = 14;
    public int MaxCell { get; set; } = 40;
    /// <summary>格内标注色号字母（A、B、C…）。</summary>
    public bool Letters { get; set; } = true;
    public bool Ruler { get; set; } = true;
    public string BrandId { get; set; } = "mard221";
    public string Title { get; set; } = "平面拼豆";
    public string Author { get; set; } = "";
    public string Notes { get; set; } = "禁止二传、二改、商用";
    public string Watermark { get; set; } = "";
    public double PitchMm { get; set; } = 2.6;

    public FlatPatternRenderOptions Clone() => (FlatPatternRenderOptions)MemberwiseClone();
}

public sealed class FlatPatternExportResult
{
    public string Folder { get; init; } = "";
    public List<string> Files { get; } = new();
}

/// <summary>
/// 平面图纸：豆格阵列 + 行列标尺 + 色号字母 + 材料表。
/// 另外输出无字母预览、CSV 清单和可打印的分页 PDF。
/// </summary>
public static class FlatPatternRenderer
{
    public static SKBitmap Render(VoxelModel m, PaletteLibrary lib, FlatPatternRenderOptions o)
    {
        var legend = ColorLegend.Build(m, lib);
        int W = m.SX, H = m.SY;
        if (W <= 0 || H <= 0) throw new InvalidOperationException("模型是空的");
        int cell = Math.Clamp((o.MaxWidth - 220) / W, o.MinCell, o.MaxCell);
        int gridW = W * cell, gridH = H * cell;
        int ruler = o.Ruler ? 40 : 0;
        int pad = 54;
        // 主体很窄时不要把整张纸拉得过宽；长图仍按需要扩展
        int sheetW = Math.Max(Math.Max(1200, Math.Min(o.MaxWidth, 2800)), gridW + ruler + pad * 2);
        int headerH = 190;
        int legendCols = sheetW >= 2800 ? 3 : sheetW >= 1400 ? 2 : 1;
        int legendRows = Math.Max(1, (legend.Entries.Count + legendCols - 1) / legendCols);
        const int legendRowH = 60;
        int legendH = 70 + legendRows * legendRowH + 16;
        int gridTop = pad + headerH + ruler;
        int legendTop = gridTop + gridH + 46;
        int sheetH = legendTop + legendH + 56;

        var bmp = new SKBitmap(sheetW, sheetH);
        using var c = new SKCanvas(bmp);
        using var k = new DrawKit();
        c.Clear(k.Paper);

        // 纸张 + 圆角卡片
        using (var shadow = DrawKit.Fill(new SKColor(0, 0, 0, 20)))
            c.DrawRoundRect(new SKRect(20 + 4, 20 + 6, sheetW - 20 + 4, sheetH - 20 + 6), 30, 30, shadow);
        using (var card = DrawKit.Fill(SKColors.White))
            c.DrawRoundRect(new SKRect(20, 20, sheetW - 20, sheetH - 20), 30, 30, card);

        // ── 标题 ──
        using (var tag = k.Text(20, k.Accent, true))
            c.DrawText("FLAT PATTERN  ·  平面拼豆", pad, pad + 24, tag);
        using (var title = k.Text(62, k.Ink, face: k.Heavy))
        {
            var text = string.IsNullOrWhiteSpace(o.Title) ? "平面拼豆" : o.Title;
            float tw = title.MeasureText(text);
            if (tw > sheetW - pad * 2 - 260) title.TextSize = 62 * (sheetW - pad * 2 - 260) / tw;
            c.DrawText(text, pad, pad + 96, title);
        }

        var libBrand = BrandTitle(lib, o.BrandId);
        double cmW = W * o.PitchMm / 10, cmH = H * o.PitchMm / 10;
        using (var sub = k.Text(25, k.Muted))
            c.DrawText($"{libBrand}  ·  {W}×{H} 颗  ·  约 {cmW:F1}×{cmH:F1} cm  ·  共 {legend.Total:N0} 颗  ·  {legend.Entries.Count} 色",
                pad, pad + 140, sub);
        using (var badge = k.Text(20, k.Blue, true, SKTextAlign.Right))
            c.DrawText($"{W} × {H}", sheetW - pad, pad + 48, badge);
        using (var badge2 = k.Text(18, k.Muted, align: SKTextAlign.Right))
            c.DrawText($"{legend.Entries.Count} 色 · {legend.Total:N0} 颗", sheetW - pad, pad + 78, badge2);

        // ── 豆格 ──
        float gx = (sheetW - gridW) / 2f, gy = gridTop;
        using (var board = DrawKit.Fill(new SKColor(0xF7, 0xF8, 0xF5)))
            c.DrawRoundRect(new SKRect(gx - 10, gy - 10, gx + gridW + 10, gy + gridH + 10), 12, 12, board);

        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                ushort ci = m.Colors[m.Index(x, y, 0)];
                if (ci == 0) continue;
                float px = gx + x * cell, py = gy + y * cell;
                using var fill = DrawKit.Fill(DrawKit.Sk(m.RgbOf(ci)));
                c.DrawRoundRect(new SKRect(px + 1.2f, py + 1.2f, px + cell - 1.2f, py + cell - 1.2f),
                    cell * 0.2f, cell * 0.2f, fill);
            }

        // 网格线：每 5 格加深
        using (var minor = DrawKit.Stroke(new SKColor(0x2D, 0x38, 0x44, 0x24), 1))
        using (var major = DrawKit.Stroke(new SKColor(0x2D, 0x38, 0x44, 0x62), 1.6f))
        {
            for (int x = 0; x <= W; x++)
            {
                var p = x % 5 == 0 ? major : minor;
                c.DrawLine(gx + x * cell, gy, gx + x * cell, gy + gridH, p);
            }
            for (int y = 0; y <= H; y++)
            {
                var p = y % 5 == 0 ? major : minor;
                c.DrawLine(gx, gy + y * cell, gx + gridW, gy + y * cell, p);
            }
        }
        using (var edge = DrawKit.Stroke(new SKColor(0x2D, 0x38, 0x44, 0xB0), 2))
            c.DrawRect(new SKRect(gx, gy, gx + gridW, gy + gridH), edge);

        // 色号字母
        if (o.Letters && cell >= 18)
        {
            float fs = Math.Clamp(cell * 0.36f, 9f, 17f);
            foreach (var e in legend.Entries)
            {
                using var p = k.Text(fs, DrawKit.LabelOn(e.Color.Rgb), true, SKTextAlign.Center);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        ushort ci = m.Colors[m.Index(x, y, 0)];
                        if (ci != e.Index) continue;
                        float px = gx + x * cell, py = gy + y * cell;
                        c.DrawText(e.Label, px + cell / 2f, py + cell / 2f + fs * 0.36f, p);
                    }
            }
        }

        // 行列标尺
        if (o.Ruler)
        {
            int step = W > 96 ? 10 : 5;
            using var num = k.Text(15, k.Muted, align: SKTextAlign.Center);
            using var tick = DrawKit.Stroke(new SKColor(0x2D, 0x38, 0x44, 0x90), 1.4f);
            for (int x = 0; x < W; x++)
            {
                if (x % step != 0 && x != W - 1) continue;
                float px = gx + x * cell + cell / 2f;
                c.DrawLine(px, gy - 6, px, gy - 1, tick);
                c.DrawText((x + 1).ToString(), px, gy - 12, num);
            }
            using var numR = k.Text(15, k.Muted, align: SKTextAlign.Right);
            for (int y = 0; y < H; y++)
            {
                if (y % step != 0 && y != H - 1) continue;
                float py = gy + y * cell + cell * 0.65f;
                c.DrawLine(gx - 6, py - 5, gx - 1, py - 5, tick);
                c.DrawText((y + 1).ToString(), gx - 10, py, numR);
            }
        }

        // ── 材料表 ──
        using (var legTitle = k.Text(30, k.Ink, true))
            c.DrawText("色号与用量", pad, legendTop + 18, legTitle);
        using (var legSub = k.Text(17, k.Muted, align: SKTextAlign.Right))
            c.DrawText("格内字母 = 色号标注；数量为实际用豆数", sheetW - pad, legendTop + 18, legSub);
        using (var line = DrawKit.Stroke(new SKColor(0x2D, 0x38, 0x44, 0x28), 1))
            c.DrawLine(pad, legendTop + 34, sheetW - pad, legendTop + 34, line);

        float colW = (sheetW - pad * 2f) / legendCols;
        string brandLabel = libBrand;
        for (int i = 0; i < legend.Entries.Count; i++)
        {
            var e = legend.Entries[i];
            int col = i % legendCols, row = i / legendCols;
            float x = pad + col * colW, y = legendTop + 52 + row * legendRowH;
            using (var sw = DrawKit.Fill(DrawKit.Sk(e.Color.Rgb)))
                c.DrawRoundRect(new SKRect(x, y, x + 36, y + 36), 7, 7, sw);
            using (var lp = k.Text(19, DrawKit.LabelOn(e.Color.Rgb), true, SKTextAlign.Center))
                c.DrawText(e.Label, x + 18, y + 25, lp);
            string code = e.BrandCodes.GetValueOrDefault(brandLabel, e.Color.Code);
            if (string.IsNullOrWhiteSpace(code)) code = "-";
            using (var cp = k.Text(21, k.Ink, true))
                c.DrawText($"{e.Label}  ·  {brandLabel} {code}", x + 46, y + 18, cp);
            using (var dp = k.Text(16, k.Muted))
                c.DrawText($"{e.Count:N0} 颗   #{e.Color.Rgb.R:X2}{e.Color.Rgb.G:X2}{e.Color.Rgb.B:X2}", x + 46, y + 38, dp);
        }

        // ── 页脚 ──
        var footParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(o.Notes)) footParts.Add(o.Notes);
        if (!string.IsNullOrWhiteSpace(o.Author)) footParts.Add(o.Author);
        footParts.Add($"豆径 {o.PitchMm} mm");
        footParts.Add("拼豆先锋 BEADWATCH 生成");
        using (var foot = k.Text(16, k.Muted, align: SKTextAlign.Center))
            c.DrawText(string.Join("  ·  ", footParts), sheetW / 2f, sheetH - 38, foot);

        if (!string.IsNullOrWhiteSpace(o.Watermark))
        {
            using var wm = k.Text(34, new SKColor(0, 0, 0, 14), true);
            c.Save();
            c.RotateDegrees(-20, sheetW / 2f, sheetH / 2f);
            for (int y = -sheetH; y < sheetH * 2; y += 230)
                for (int x = -sheetW; x < sheetW * 2; x += 560)
                    c.DrawText(o.Watermark, x + (y / 230 % 2) * 280, y, wm);
            c.Restore();
        }
        return bmp;
    }

    public static FlatPatternExportResult Export(string folder, VoxelModel m, PaletteLibrary lib, FlatPatternRenderOptions o,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(folder);
        var outp = new FlatPatternExportResult { Folder = folder };

        progress?.Report("绘制平面图纸…");
        using var full = Render(m, lib, o);
        var main = Path.Combine(folder, "01_平面图纸.png");
        SavePng(full, main);
        outp.Files.Add(main);

        progress?.Report("绘制预览图…");
        var prevOpt = o.Clone();
        prevOpt.MaxWidth = 1500;
        prevOpt.Letters = false;
        using (var prev = Render(m, lib, prevOpt))
        {
            var p = Path.Combine(folder, "02_平面预览.png");
            SavePng(prev, p);
            outp.Files.Add(p);
        }

        progress?.Report("写出材料清单…");
        var csv = Path.Combine(folder, "材料清单.csv");
        WriteCsv(csv, m, lib, o);
        outp.Files.Add(csv);

        progress?.Report("生成 PDF…");
        var pdf = Path.Combine(folder, "平面图纸.pdf");
        WritePdf(full, pdf, o.Title);
        outp.Files.Add(pdf);
        return outp;
    }

    private static void SavePng(SKBitmap bmp, string path)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    private static void WriteCsv(string path, VoxelModel m, PaletteLibrary lib, FlatPatternRenderOptions o)
    {
        var legend = ColorLegend.Build(m, lib);
        string brandLabel = BrandTitle(lib, o.BrandId);
        var sb = new StringBuilder();
        sb.AppendLine("标注,品牌,色号,RGB,用量");
        foreach (var e in legend.Entries)
        {
            string code = e.BrandCodes.GetValueOrDefault(brandLabel, e.Color.Code);
            sb.AppendLine($"{e.Label},{brandLabel},{code},\"{e.Color.Rgb.R},{e.Color.Rgb.G},{e.Color.Rgb.B}\",{e.Count}");
        }
        sb.AppendLine($"合计,,,,\"{legend.Total}\"");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void WritePdf(SKBitmap bitmap, string path, string title)
    {
        const float pageW = 842, pageH = 1191, margin = 24;
        using var stream = new SKFileWStream(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = title, Author = "拼豆先锋", Creator = "拼豆先锋 BEADWATCH", RasterDpi = 220,
        });
        float scale = (pageW - 2 * margin) / bitmap.Width;
        float sliceH = (pageH - 2 * margin) / scale;
        for (float top = 0; top < bitmap.Height; top += sliceH)
        {
            using var c = doc.BeginPage(pageW, pageH);
            c.Clear(SKColors.White);
            c.Translate(margin, margin);
            c.Scale(scale);
            c.ClipRect(SKRect.Create(0, 0, bitmap.Width, Math.Min(sliceH, bitmap.Height - top)));
            c.DrawBitmap(bitmap, 0, -top);
            doc.EndPage();
        }
        doc.Close();
    }

    private static string BrandTitle(PaletteLibrary lib, string id)
    {
        foreach (var (bid, label) in PaletteLibrary.DrawingBrands)
            if (bid.Equals(id, StringComparison.OrdinalIgnoreCase)) return label;
        return lib.Brands.TryGetValue(id, out var b) && !string.IsNullOrWhiteSpace(b.Title) ? b.Title : id;
    }
}
