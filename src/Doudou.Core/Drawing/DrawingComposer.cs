using System.Numerics;
using System.Text;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.IO;
using Doudou.Core.Model;
using Doudou.Core.Parts;
using Doudou.Core.Render;
using SkiaSharp;

namespace Doudou.Core.Drawing;

public sealed class DrawingOptions
{
    public int Width { get; set; } = 4200;
    /// <summary>零件颗粒图单格像素。</summary>
    public int Cell { get; set; } = 26;
    public int LayerCell { get; set; } = 22;
    public bool CellLetters { get; set; } = true;
    public float ExplodeSpread { get; set; } = 2.0f;
    public int StepColumns { get; set; } = 6;
    public double PitchMm { get; set; } = 2.6;
    public bool Pdf { get; set; } = true;
    public bool Tint { get; set; }
}

/// <summary>
/// 图纸生成：仿照真人画手的立体拼豆图纸版式——
/// 标题与材料表 / 零件颗粒图 / 爆炸图 / 装配步骤 / 成品效果，另出分层颗粒图、清单 CSV、整卷 PDF。
/// </summary>
public sealed class DrawingComposer : IDisposable
{
    private readonly VoxelModel _m;
    private readonly ProjectMeta _meta;
    private readonly PaletteLibrary _lib;
    private readonly DrawingOptions _o;
    private readonly DrawKit _k = new();
    private readonly ColorLegend _legend;
    private readonly ModelPartsReport _rep;
    private readonly AssemblyPlan _plan;

    public ColorLegend Legend => _legend;
    public ModelPartsReport Parts => _rep;
    public AssemblyPlan Plan => _plan;

    public DrawingComposer(VoxelModel m, ProjectMeta meta, PaletteLibrary lib, DrawingOptions? o = null)
    {
        _m = m; _meta = meta; _lib = lib; _o = o ?? new DrawingOptions();
        _legend = ColorLegend.Build(m, lib);
        _rep = PartAnalyzer.Analyze(m, writeBackAxis: false);
        if (_rep.Parts.Count == 0)
        {
            // 尚未拆件：整体当成一个虚拟零件，图纸仍能出（但会提示）
            var all = new PartReport { Id = 0, Name = "整体", Cells = m.SolidIndices().ToList() };
            var b = m.Bounds();
            if (b != null) { all.Min = b.Value.Min; all.Max = b.Value.Max; }
            _rep.Parts.Add(all);
        }
        _plan = AssemblyPlanner.Plan(m, _rep);
    }

    // ═════════════════════════ 输出 ═════════════════════════

    public sealed class Output
    {
        public List<string> Files = new();
        public string Folder = "";
    }

    public Output ExportAll(string folder, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(folder);
        var outp = new Output { Folder = folder };
        string Save(SKBitmap bmp, string name)
        {
            var p = Path.Combine(folder, name);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            using var f = File.Create(p);
            data.SaveTo(f);
            outp.Files.Add(p);
            return p;
        }

        progress?.Report("绘制标题与材料表…");
        using var header = Header();
        progress?.Report("绘制零件颗粒图…");
        using var parts = PartsSection();
        progress?.Report("绘制爆炸图…");
        using var exploded = ExplodedSection();
        progress?.Report("绘制装配步骤…");
        using var steps = StepsSection();
        progress?.Report("绘制成品效果…");
        using var show = ShowcaseSection();
        progress?.Report("绘制分层颗粒图…");
        using var layers = LayersSection();
        using var sizes = SizeTableSection();

        progress?.Report("拼合总图…");
        var main = new[] { header, parts, exploded, show };
        using (var poster = Stack(main)) Save(poster, "01_总图.png");
        using (var p = Stack(new[] { header, parts })) Save(p, "02_零部件拆分图.png");
        using (var p = Stack(new[] { layers })) Save(p, "03_分层颗粒图.png");
        using (var p = Stack(new[] { exploded, steps, show })) Save(p, "04_整体拼装示意图.png");
        using (var p = Stack(new[] { sizes })) Save(p, "05_材料尺寸清单.png");

        // 单件图
        var partDir = Path.Combine(folder, "零件");
        Directory.CreateDirectory(partDir);
        foreach (var pr in _rep.Parts)
        {
            using var b = SinglePart(pr);
            var p = Path.Combine(partDir, $"{Sanitize(pr.Name)}.png");
            using var data = b.Encode(SKEncodedImageFormat.Png, 100);
            using var f = File.Create(p);
            data.SaveTo(f);
        }

        progress?.Report("写出清单…");
        outp.Files.Add(WriteBom(Path.Combine(folder, "材料清单.csv")));
        outp.Files.Add(WriteSizes(Path.Combine(folder, "尺寸清单.csv")));
        outp.Files.Add(WriteAssemblyText(Path.Combine(folder, "装配步骤.txt")));

        if (_o.Pdf)
        {
            progress?.Report("生成 PDF…");
            var pdf = Path.Combine(folder, "图纸.pdf");
            WritePdf(pdf, new[] { header, parts, layers, exploded, steps, show, sizes });
            outp.Files.Add(pdf);
        }
        return outp;
    }

    private static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>把若干分区竖向拼成一张长图，带底纹与水印。</summary>
    public SKBitmap Stack(IReadOnlyList<SKBitmap> sections)
    {
        int gap = 40, pad = 60;
        int w = _o.Width + pad * 2;
        int h = pad * 2 + sections.Sum(s => s.Height) + gap * (sections.Count - 1) + 80;
        var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        PaperBackground(c, w, h);
        float y = pad;
        foreach (var s in sections) { c.DrawBitmap(s, pad, y); y += s.Height + gap; }
        using var foot = _k.Text(30, _k.Muted, align: SKTextAlign.Center);
        var copy = $"{_meta.Title}  ·  {(_meta.Author.Length > 0 ? _meta.Author + "  ·  " : "")}{_meta.Notes}  ·  拼豆先锋 BEADWATCH 生成";
        c.DrawText(copy, w / 2f, h - 50, foot);
        if (!string.IsNullOrWhiteSpace(_meta.Watermark)) Watermark(c, w, h, _meta.Watermark);
        return bmp;
    }

    private void PaperBackground(SKCanvas c, int w, int h)
    {
        c.Clear(_k.Paper);
        using var dot = DrawKit.Fill(new SKColor(0, 0, 0, 10));
        for (int y = 0; y < h; y += 36)
            for (int x = (y / 36) % 2 * 18; x < w; x += 36)
                c.DrawCircle(x, y, 2.2f, dot);
    }

    private void Watermark(SKCanvas c, int w, int h, string text)
    {
        using var p = _k.Text(34, new SKColor(0, 0, 0, 16), true);
        c.Save();
        c.RotateDegrees(-20, w / 2f, h / 2f);
        for (int y = -h; y < h * 2; y += 220)
            for (int x = -w; x < w * 2; x += 520)
                c.DrawText(text, x + (y / 220 % 2) * 260, y, p);
        c.Restore();
    }

    // ═════════════════════════ ① 标题 + 材料表 ═════════════════════════

    public SKBitmap Header()
    {
        int W = _o.Width;
        // 右侧材料表：每段最多 12 色
        const int perBand = 12;
        int bands = Math.Max(1, (int)Math.Ceiling(_legend.Entries.Count / (double)perBand));
        var brandLabels = PaletteLibrary.DrawingBrands.Select(b => b.Label).Where(l => _legend.Entries.Count == 0 || _legend.Entries[0].BrandCodes.ContainsKey(l)).ToList();
        int rowH = 50, colW = 112, headW = 150;
        int bandH = rowH * (3 + brandLabels.Count) + 30;
        int tableH = bands * bandH;
        int H = Math.Max(900, tableH + 260);
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        _k.DrawCard(c, new SKRect(0, 0, W, H));

        // 左：成品图
        using (var hero = IsoRenderer.Render(IsoRenderer.FromModel(_m).ToList(), new RenderOptions { FitSize = 820, Look = BeadLook.Bead, Yaw = 30, Pitch = 22 }))
        {
            var r = Fit(hero.Bitmap, new SKRect(40, 40, 900, H - 40));
            using var bg = DrawKit.Fill(new SKColor(0xF6, 0xF7, 0xF4));
            c.DrawRoundRect(new SKRect(40, 40, 900, H - 40), 24, 24, bg);
            c.DrawBitmap(hero.Bitmap, r);
        }

        // 中：标题
        float tx = 960;
        using (var title = _k.Text(150, _k.Ink, face: _k.Heavy))
        {
            float tw = title.MeasureText(_meta.Title);
            if (tw > 1500) title.TextSize = 150 * 1500 / tw;
            c.DrawText(_meta.Title, tx, 230, title);
        }
        using (var sub = _k.Text(54, _k.Accent, true))
        {
            var who = new List<string>();
            if (!string.IsNullOrWhiteSpace(_meta.Modeler)) who.Add("建模：" + _meta.Modeler);
            if (!string.IsNullOrWhiteSpace(_meta.Author)) who.Add("拆图：" + _meta.Author);
            c.DrawText(who.Count > 0 ? string.Join("    ", who) : "立体拼豆图纸", tx, 330, sub);
        }
        using (var info = _k.Text(40, _k.Muted))
        {
            var b = _m.Bounds();
            var size = b == null ? new Int3(0, 0, 0) : b.Value.Max - b.Value.Min + new Int3(1, 1, 1);
            double mm = _o.PitchMm;
            c.DrawText($"成品尺寸 {size.X}×{size.Y}×{size.Z} 颗 ≈ {size.X * mm / 10:F1}×{size.Y * mm / 10:F1}×{size.Z * mm / 10:F1} cm", tx, 420, info);
            c.DrawText($"总豆量 {_legend.Total} 颗 · {_legend.Entries.Count} 色 · {_rep.Parts.Count} 个零件", tx, 480, info);
            c.DrawText($"色卡 {_lib.Get(_m.BrandId).Title}（其他品牌为就近对照，仅供参考）", tx, 540, info);
            if (!string.IsNullOrWhiteSpace(_meta.Serial)) c.DrawText("编号 " + _meta.Serial, tx, 600, info);
        }
        using (var warn = _k.Text(42, _k.Accent, true))
            c.DrawText(_meta.Notes, tx, H - 70, warn);

        // 右：材料表
        float x0 = W - 40 - headW - colW * Math.Min(perBand, Math.Max(1, _legend.Entries.Count)) - colW;
        float y0 = 60;
        using var line = DrawKit.Stroke(new SKColor(0x9A, 0xA3, 0xAD), 2);
        using var cellTxt = _k.Text(30, _k.Ink, align: SKTextAlign.Center);
        using var headTxt = _k.Text(30, _k.Ink, true, SKTextAlign.Center);
        for (int band = 0; band < bands; band++)
        {
            var entries = _legend.Entries.Skip(band * perBand).Take(perBand).ToList();
            float y = y0 + band * bandH;
            var rows = new List<string> { "色号标注", "色板" };
            rows.AddRange(brandLabels);
            rows.Add("用量");
            for (int r = 0; r < rows.Count; r++)
            {
                float ry = y + r * rowH;
                c.DrawRect(x0, ry, headW, rowH, line);
                c.DrawText(rows[r], x0 + headW / 2f, ry + rowH * 0.68f, headTxt);
                for (int k = 0; k < entries.Count; k++)
                {
                    var e = entries[k];
                    float cx = x0 + headW + k * colW;
                    var rect = new SKRect(cx, ry, cx + colW, ry + rowH);
                    if (r == 1)
                    {
                        using var sw = DrawKit.Fill(DrawKit.Sk(e.Color.Rgb));
                        c.DrawRect(rect, sw);
                    }
                    c.DrawRect(rect, line);
                    string txt = r == 0 ? e.Label : r == 1 ? "" : r == rows.Count - 1 ? e.Count.ToString() : e.BrandCodes.GetValueOrDefault(rows[r], "-");
                    var p = r == 0 || r == rows.Count - 1 ? headTxt : cellTxt;
                    if (r == rows.Count - 1) p.Color = _k.Accent;
                    c.DrawText(txt, rect.MidX, ry + rowH * 0.68f, p);
                    p.Color = _k.Ink;
                }
            }
            if (band == bands - 1)
            {
                float cx = x0 + headW + Math.Min(perBand, Math.Max(1, _legend.Entries.Count)) * colW + 10;
                using var tot = _k.Text(32, _k.Ink, true, SKTextAlign.Center);
                c.DrawText("合计", cx + colW / 2f, y + rowH * 0.7f, tot);
                tot.Color = _k.Accent; tot.TextSize = 40;
                c.DrawText(_legend.Total.ToString(), cx + colW / 2f, y + rowH * 1.9f, tot);
            }
        }
        return bmp;
    }

    private static SKRect Fit(SKBitmap b, SKRect box)
    {
        float s = Math.Min(box.Width / b.Width, box.Height / b.Height);
        float w = b.Width * s, h = b.Height * s;
        return SKRect.Create(box.MidX - w / 2, box.MidY - h / 2, w, h);
    }

    // ═════════════════════════ ② 零件颗粒图 ═════════════════════════

    /// <summary>画一块板（行列矩阵），左上角 (x, y)。返回实际尺寸。</summary>
    public SKSize DrawSheet(SKCanvas c, float x, float y, ushort[,] g, int cell, bool letters, bool coords = false)
    {
        int rows = g.GetLength(0), cols = g.GetLength(1);
        using var fill = DrawKit.Fill(SKColors.White);
        using var border = DrawKit.Stroke(new SKColor(0x55, 0x5D, 0x66), Math.Max(1, cell / 16f));
        using var txt = _k.Text(cell * 0.56f, SKColors.Black, true, SKTextAlign.Center);
        using var ghost = DrawKit.Stroke(new SKColor(0xE4, 0xE7, 0xEB), 1);
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                var idx = g[r, col];
                var rect = SKRect.Create(x + col * cell, y + r * cell, cell, cell);
                if (idx == 0) { if (coords) c.DrawRect(rect, ghost); continue; }
                var bc = _m.Palette[idx];
                var rgb = bc.IsAny ? new Rgb(70, 82, 78) : bc.Rgb;
                fill.Color = DrawKit.Sk(rgb);
                c.DrawRect(rect, fill);
                c.DrawRect(rect, border);
                if (letters && cell >= 14)
                {
                    var e = _legend.Of(idx);
                    if (e != null)
                    {
                        txt.Color = DrawKit.LabelOn(rgb);
                        c.DrawText(e.Label, rect.MidX, rect.MidY + cell * 0.2f, txt);
                    }
                }
            }
        if (coords)
        {
            using var thick = DrawKit.Stroke(new SKColor(0x8E, 0x97, 0xA3), 2);
            using var num = _k.Text(Math.Max(10, cell * 0.42f), _k.Muted, align: SKTextAlign.Center);
            for (int col = 0; col <= cols; col += 5)
            {
                c.DrawLine(x + col * cell, y, x + col * cell, y + rows * cell, thick);
                if (col < cols) c.DrawText((col + 1).ToString(), x + col * cell + cell / 2f, y - 6, num);
            }
            for (int r = 0; r <= rows; r += 5)
            {
                c.DrawLine(x, y + r * cell, x + cols * cell, y + r * cell, thick);
                if (r < rows) c.DrawText((r + 1).ToString(), x - cell * 0.7f, y + r * cell + cell * 0.7f, num);
            }
        }
        return new SKSize(cols * cell, rows * cell);
    }

    private ushort[,] SheetOf(PartReport pr)
    {
        var info = _m.FindPart(pr.Id);
        return PartAnalyzer.Sheet(_m, pr, info?.Flip ?? false);
    }

    public SKBitmap PartsSection()
    {
        int W = _o.Width, cell = _o.Cell, pad = 60, labelH = 60, gapX = 50, gapY = 40;
        // 自动缩小格子，避免最大零件超宽
        int maxCols = _rep.Parts.Max(p => SheetOf(p).GetLength(1));
        if (maxCols * cell > W - 2 * pad) cell = Math.Max(10, (W - 2 * pad) / maxCols);

        var groups = _rep.Parts.GroupBy(p => p.PlaneAxis).OrderBy(g => g.Key == 1 ? 0 : g.Key == 2 ? 1 : g.Key == 0 ? 2 : 3).ToList();
        // 预排版
        var placements = new List<(PartReport P, ushort[,] G, float X, float Y)>();
        var headings = new List<(string Text, float Y)>();
        float y = 130;
        foreach (var grp in groups)
        {
            headings.Add(($"{SplitTools.AxisChinese(grp.Key)}（{grp.Count()} 件）", y));
            y += 70;
            float x = pad, rowH = 0;
            foreach (var pr in grp.OrderByDescending(p => SheetOf(p).GetLength(0)).ThenBy(p => p.Name))
            {
                var g = SheetOf(pr);
                float w = Math.Max(g.GetLength(1) * cell, 140), h = g.GetLength(0) * cell + labelH;
                if (x + w > W - pad && x > pad) { x = pad; y += rowH + gapY; rowH = 0; }
                placements.Add((pr, g, x, y));
                x += w + gapX;
                rowH = Math.Max(rowH, h);
            }
            y += rowH + gapY + 30;
        }
        int H = (int)y + 40;
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, H), "零件颗粒图");
        using (var hint = _k.Text(34, _k.Muted))
            c.DrawText("每块为一张可独立熨烫的平板；格内字母对应材料表色号；标「翻」的零件熨烫后需翻面使用", 520, 76, hint);
        using var head = _k.Text(40, _k.Ink, true);
        foreach (var (t, hy) in headings) c.DrawText(t, pad, hy + 44, head);
        using var name = _k.Text(36, _k.Ink, true);
        using var meta = _k.Text(26, _k.Muted);
        using var flip = _k.Text(30, _k.Accent, true);
        foreach (var (pr, g, px, py) in placements)
        {
            c.DrawText(pr.Name, px, py + 38, name);
            float nw = name.MeasureText(pr.Name);
            c.DrawText($"{pr.Beads}颗", px + nw + 12, py + 38, meta);
            var info = _m.FindPart(pr.Id);
            if (info?.Flip == true) c.DrawText("翻", px + nw + 12 + meta.MeasureText($"{pr.Beads}颗") + 10, py + 38, flip);
            if (pr.Issues.Any(i => i.Level == IssueLevel.Error))
            {
                using var bad = DrawKit.Stroke(_k.Accent, 4);
                c.DrawRect(px - 6, py + labelH - 6, g.GetLength(1) * cell + 12, g.GetLength(0) * cell + 12, bad);
            }
            DrawSheet(c, px, py + labelH, g, cell, _o.CellLetters);
        }
        return bmp;
    }

    public SKBitmap SinglePart(PartReport pr)
    {
        var g = SheetOf(pr);
        int cell = Math.Max(_o.Cell, 36);
        int w = g.GetLength(1) * cell + 160, h = g.GetLength(0) * cell + 260;
        w = Math.Max(w, 700);
        var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        using var t = _k.Text(44, _k.Ink, true);
        c.DrawText($"{pr.Name}  {SplitTools.AxisChinese(pr.PlaneAxis)}  {g.GetLength(1)}×{g.GetLength(0)}  {pr.Beads}颗", 40, 70, t);
        DrawSheet(c, 90, 150, g, cell, true, coords: true);
        // 本件用色
        using var u = _k.Text(28, _k.Muted);
        float x = 40;
        foreach (var (idx, cnt) in pr.ColorUsage.OrderByDescending(kv => kv.Value))
        {
            var e = _legend.Of(idx);
            if (e == null) continue;
            var s = $"{e.Label}={e.Color} ×{cnt}";
            c.DrawText(s, x, h - 30, u);
            x += u.MeasureText(s) + 30;
        }
        return bmp;
    }

    // ═════════════════════════ ③ 爆炸图 ═════════════════════════

    public SKBitmap ExplodedSection()
    {
        int W = _o.Width;
        // 零件多时（几十层环片）拉开幅度要收敛，否则爆炸图会变成细长的一条
        int pc = _rep.Parts.Count;
        float spread = pc > 80 ? Math.Min(_o.ExplodeSpread, 1.3f) : pc > 40 ? Math.Min(_o.ExplodeSpread, 1.5f) : _o.ExplodeSpread;
        var lay = Exploded.Build(_m, _rep, spread, tint: _o.Tint);
        var complexParts = _plan.Steps.Where(s => s.Status == StepStatus.Complex).Select(s => s.PartId).ToHashSet();
        using var r = IsoRenderer.Render(lay.Beads, new RenderOptions { FitSize = 2600, Yaw = 38, Pitch = 26, Look = BeadLook.Cube });
        float scale = Math.Min(1f, (W - 200f) / r.Bitmap.Width);
        int imgH = (int)(r.Bitmap.Height * scale);
        int H = imgH + 260;
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, H), "爆炸图");
        using (var hint = _k.Text(34, _k.Muted))
            c.DrawText("零件按装配方向拉开；红色标签 = 需要试装或点胶的零件，蓝色细线指向零件在成品中的位置", 360, 76, hint);
        float ox = (W - r.Bitmap.Width * scale) / 2, oy = 140;
        var dst = SKRect.Create(ox, oy, r.Bitmap.Width * scale, imgH);
        c.DrawBitmap(r.Bitmap, dst);

        // 标签与引线（简单避让：同一位置附近已有标签就往下挪）
        var used = new List<SKRect>();
        using var lbl = _k.Text(30, _k.Ink, true, SKTextAlign.Center);
        using var leader = DrawKit.Stroke(_k.Blue, 2);
        using var box = DrawKit.Fill(new SKColor(255, 255, 255, 230));
        using var boxLine = DrawKit.Stroke(_k.Blue, 2);
        using var boxBad = DrawKit.Stroke(_k.Accent, 3);
        var center = new SKPoint(dst.MidX, dst.MidY);
        // 标签太多会糊成一片：零件多时只标较大的零件和需要注意的零件
        var labelled = _rep.Parts.OrderByDescending(p => complexParts.Contains(p.Id)).ThenByDescending(p => p.Beads).Take(45).Select(p => p.Id).ToHashSet();
        foreach (var pr in _rep.Parts)
        {
            if (!labelled.Contains(pr.Id)) continue;
            if (!lay.Anchors.TryGetValue(pr.Id, out var a)) continue;
            var sp = r.Project(a);
            var p = new SKPoint(ox + sp.X * scale, oy + sp.Y * scale);
            var dir = p - center;
            float len = MathF.Max(1, MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
            var lp = new SKPoint(p.X + dir.X / len * 70, p.Y + dir.Y / len * 70);
            float tw = lbl.MeasureText(pr.Name) + 24;
            var rect = SKRect.Create(lp.X - tw / 2, lp.Y - 24, tw, 44);
            for (int tries = 0; tries < 20 && used.Any(u => u.IntersectsWith(rect)); tries++) rect.Offset(0, 46);
            used.Add(rect);
            c.DrawLine(p, new SKPoint(rect.MidX, rect.MidY), leader);
            c.DrawCircle(p, 5, DrawKit.Fill(_k.Blue));
            c.DrawRoundRect(rect, 10, 10, box);
            c.DrawRoundRect(rect, 10, 10, complexParts.Contains(pr.Id) ? boxBad : boxLine);
            c.DrawText(pr.Name, rect.MidX, rect.MidY + 11, lbl);
        }
        return bmp;
    }

    // ═════════════════════════ ④ 装配步骤 ═════════════════════════

    public SKBitmap StepsSection()
    {
        int W = _o.Width, cols = Math.Max(2, _o.StepColumns);
        float cw = (W - 80f) / cols, ch = cw * 0.95f + 120;
        int rows = (int)Math.Ceiling(_plan.Steps.Count / (double)cols);
        int H = (int)(160 + rows * ch + 40);
        var bmp = new SKBitmap(W, Math.Max(H, 300));
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, bmp.Height), "装配步骤");
        using (var hint = _k.Text(34, _k.Muted))
            c.DrawText(_plan.Summary, 360, 76, hint);
        using var num = _k.Text(36, _k.Accent, true);
        using var txt = _k.Text(26, _k.Ink);
        var placed = new HashSet<int>();
        for (int s = 0; s < _plan.Steps.Count; s++)
        {
            var st = _plan.Steps[s];
            placed.Add(st.PartId);
            float x = 40 + (s % cols) * cw, y = 140 + (s / cols) * ch;
            var beads = new List<RenderBead>();
            foreach (var pr in _rep.Parts)
            {
                if (!placed.Contains(pr.Id)) continue;
                bool cur = pr.Id == st.PartId;
                foreach (var i in pr.Cells)
                {
                    var col = _m.RgbOf(_m.Colors[i]);
                    if (!cur) col = Rgb.Lerp(col, new Rgb(235, 238, 240), 0.55);
                    // 当前步的零件沿装入方向的反方向稍微拉开，表达「从这里装进来」
                    var off = cur && st.InsertDir >= 0 ? -ToVec(Dir6.All[st.InsertDir]) * 3 : Vector3.Zero;
                    beads.Add(new RenderBead { Cell = _m.Pos(i), Offset = off, Color = col, Alpha = 255, Group = pr.Id });
                }
            }
            using var r = IsoRenderer.Render(beads, new RenderOptions { FitSize = (int)(cw - 20), Yaw = 35, Pitch = 25, Margin = 10 });
            var dst = Fit(r.Bitmap, SKRect.Create(x + 10, y + 50, cw - 20, cw * 0.85f));
            using (var bg = DrawKit.Fill(new SKColor(0xF7, 0xF8, 0xF6))) c.DrawRoundRect(SKRect.Create(x + 6, y, cw - 12, ch - 20), 16, 16, bg);
            c.DrawBitmap(r.Bitmap, dst);
            c.DrawText($"{st.Index:00}", x + 20, y + 44, num);
            using var nm = _k.Text(32, _k.Ink, true);
            c.DrawText(st.PartName, x + 90, y + 42, nm);
            if (st.Status == StepStatus.Complex)
            {
                using var bad = _k.Text(26, _k.Accent, true);
                c.DrawText("需点胶", x + cw - 120, y + 42, bad);
            }
            WrapText(c, st.Instruction, x + 20, y + 50 + cw * 0.85f + 30, cw - 40, txt, 2);
        }
        return bmp;
    }

    private static Vector3 ToVec(Int3 d) => new(d.X, d.Y, d.Z);

    private static void WrapText(SKCanvas c, string s, float x, float y, float maxW, SKPaint p, int maxLines)
    {
        var line = new StringBuilder();
        int lines = 0;
        foreach (var ch in s)
        {
            line.Append(ch);
            if (p.MeasureText(line.ToString()) <= maxW) continue;
            line.Length--;
            if (lines == maxLines - 1)
            {
                c.DrawText(line.ToString().TrimEnd() + "…", x, y, p);
                return;
            }
            c.DrawText(line.ToString(), x, y, p);
            y += p.TextSize * 1.35f;
            lines++;
            line.Clear(); line.Append(ch);
        }
        if (line.Length > 0) c.DrawText(line.ToString(), x, y, p);
    }

    // ═════════════════════════ ⑤ 成品效果 ═════════════════════════

    public SKBitmap ShowcaseSection()
    {
        int W = _o.Width;
        var views = new (float Yaw, float Pitch, string Name)[] { (30, 22, "斜前方"), (0, 0, "正面"), (90, 0, "左侧"), (180, 0, "背面"), (210, 22, "斜后方") };
        float cw = (W - 80f) / views.Length;
        int H = (int)(cw * 1.25f + 200);
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, H), "成品效果");
        var beads = IsoRenderer.FromModel(_m).ToList();
        using var cap = _k.Text(36, _k.Muted, true, SKTextAlign.Center);
        for (int i = 0; i < views.Length; i++)
        {
            var (yaw, pitch, name) = views[i];
            using var r = IsoRenderer.Render(beads, new RenderOptions { Yaw = yaw, Pitch = pitch, FitSize = (int)cw, Look = BeadLook.Bead });
            var dst = Fit(r.Bitmap, SKRect.Create(40 + i * cw + 10, 130, cw - 20, cw * 1.2f - 20));
            c.DrawBitmap(r.Bitmap, dst);
            c.DrawText(name, 40 + i * cw + cw / 2, H - 40, cap);
        }
        return bmp;
    }

    // ═════════════════════════ ⑥ 分层颗粒图 ═════════════════════════

    /// <summary>整个模型自下而上逐层（Y 层）的颗粒图，用于对照与核数。</summary>
    public SKBitmap LayersSection()
    {
        int W = _o.Width, cell = _o.LayerCell, pad = 60, gap = 50;
        var b = _m.Bounds();
        if (b == null) return new SKBitmap(W, 200);
        var (mn, mx) = b.Value;
        int cols = mx.X - mn.X + 1, rows = mx.Z - mn.Z + 1;
        if (cols * cell > W - 2 * pad) cell = Math.Max(8, (W - 2 * pad) / cols);
        float bw = cols * cell + gap, bh = rows * cell + 70 + gap;
        int perRow = Math.Max(1, (int)((W - 2 * pad + gap) / bw));
        int layers = mx.Y - mn.Y + 1;
        int H = (int)(150 + Math.Ceiling(layers / (double)perRow) * bh + 40);
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, H), "分层颗粒图");
        using (var hint = _k.Text(34, _k.Muted))
            c.DrawText("自下而上逐层俯视（上方为背面、下方为正面），用于核对整体豆数与配色", 420, 76, hint);
        using var t = _k.Text(32, _k.Ink, true);
        using var tm = _k.Text(26, _k.Muted);
        for (int L = 0; L < layers; L++)
        {
            int yv = mn.Y + L;
            var g = new ushort[rows, cols];
            int count = 0;
            for (int z = mn.Z; z <= mx.Z; z++)
                for (int x = mn.X; x <= mx.X; x++)
                {
                    var v = _m.Get(x, yv, z);
                    g[z - mn.Z, x - mn.X] = v;
                    if (v != 0) count++;
                }
            float px = pad + (L % perRow) * bw, py = 130 + (L / perRow) * bh;
            c.DrawText($"第 {L + 1} 层", px, py + 40, t);
            c.DrawText($"{count}颗", px + t.MeasureText($"第 {L + 1} 层") + 12, py + 40, tm);
            DrawSheet(c, px, py + 60, g, cell, _o.CellLetters && cell >= 14, coords: false);
        }
        return bmp;
    }

    // ═════════════════════════ ⑦ 材料 / 尺寸清单 ═════════════════════════

    public SKBitmap SizeTableSection()
    {
        int W = _o.Width, rowH = 52;
        var cols = new[] { ("零件", 220f), ("类型", 220f), ("板面（列×行）", 320f), ("实际尺寸 mm", 380f), ("豆数", 180f), ("装配", 1100f), ("用色", 1600f) };
        int H = 200 + rowH * (_rep.Parts.Count + 1) + 40;
        var bmp = new SKBitmap(W, H);
        using var c = new SKCanvas(bmp);
        _k.DrawCard(c, new SKRect(0, 0, W, H), "材料 / 尺寸清单");
        using (var hint = _k.Text(34, _k.Muted))
            c.DrawText($"豆径 {_o.PitchMm} mm；尺寸按豆数 × 豆径估算，熨烫收缩约 3%~5%", 480, 76, hint);
        using var head = _k.Text(30, _k.Ink, true);
        using var cell = _k.Text(28, _k.Ink);
        using var line = DrawKit.Stroke(_k.GridLine, 2);
        float y = 140;
        float x = 50;
        foreach (var (name, w) in cols) { c.DrawText(name, x + 10, y + 36, head); x += w; }
        c.DrawLine(50, y + rowH, W - 50, y + rowH, line);
        var stepOf = _plan.Steps.ToDictionary(s => s.PartId, s => s);
        foreach (var pr in _rep.Parts)
        {
            y += rowH;
            var (cc, rr) = pr.SheetSize;
            var vals = new[]
            {
                pr.Name, SplitTools.AxisChinese(pr.PlaneAxis), $"{cc} × {rr}",
                $"{cc * _o.PitchMm:F0} × {rr * _o.PitchMm:F0}", pr.Beads.ToString(),
                stepOf.TryGetValue(pr.Id, out var st) ? $"第{st.Index}步 · {(st.Status == StepStatus.Complex ? "需点胶" : st.Status == StepStatus.Base ? "基准" : "直线装入")}" : "-",
                string.Join(" ", pr.ColorUsage.OrderByDescending(kv => kv.Value).Select(kv => $"{_legend.Of(kv.Key)?.Label}{kv.Value}")),
            };
            x = 50;
            for (int k = 0; k < cols.Length; k++)
            {
                var s = vals[k];
                while (s.Length > 2 && cell.MeasureText(s) > cols[k].Item2 - 20) s = s[..^2];
                c.DrawText(s, x + 10, y + 36, cell);
                x += cols[k].Item2;
            }
            c.DrawLine(50, y + rowH, W - 50, y + rowH, line);
        }
        return bmp;
    }

    public string WriteBom(string path)
    {
        var sb = new StringBuilder();
        var brandCols = PaletteLibrary.DrawingBrands.Select(b => b.Label).ToList();
        sb.AppendLine("标注,色号," + string.Join(",", brandCols) + ",RGB,用量,分件分布");
        foreach (var e in _legend.Entries)
        {
            var dist = string.Join(";", _rep.Parts.Where(p => p.ColorUsage.ContainsKey(e.Index)).Select(p => $"{p.Name}:{p.ColorUsage[e.Index]}"));
            sb.AppendLine($"{e.Label},{e.Color},{string.Join(",", brandCols.Select(b => e.BrandCodes.GetValueOrDefault(b, "-")))}," +
                          $"\"{e.Color.Rgb.R},{e.Color.Rgb.G},{e.Color.Rgb.B}\",{e.Count},\"{dist}\"");
        }
        sb.AppendLine($"合计,,{string.Join(",", brandCols.Select(_ => ""))},,{_legend.Total},");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public string WriteSizes(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("零件,类型,列,行,宽mm,高mm,豆数,装配步骤,装配说明");
        var stepOf = _plan.Steps.ToDictionary(s => s.PartId, s => s);
        foreach (var pr in _rep.Parts)
        {
            var (c, r) = pr.SheetSize;
            stepOf.TryGetValue(pr.Id, out var st);
            sb.AppendLine($"{pr.Name},{SplitTools.AxisChinese(pr.PlaneAxis)},{c},{r},{c * _o.PitchMm:F1},{r * _o.PitchMm:F1},{pr.Beads},{st?.Index},\"{st?.Instruction}\"");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public string WriteAssemblyText(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_meta.Title} 装配步骤");
        sb.AppendLine(_plan.Summary);
        sb.AppendLine();
        foreach (var s in _plan.Steps) sb.AppendLine($"{s.Index:00}. {s.Instruction}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>A3 纵向 PDF：每个分区按页宽缩放，过长时分页。</summary>
    public void WritePdf(string path, IReadOnlyList<SKBitmap> sections)
    {
        const float pageW = 842, pageH = 1191, margin = 28;
        using var stream = new SKFileWStream(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = _meta.Title, Author = _meta.Author, Creator = "拼豆先锋 BEADWATCH", RasterDpi = 220 });
        foreach (var s in sections)
        {
            float scale = (pageW - 2 * margin) / s.Width;
            float sliceH = (pageH - 2 * margin) / scale;
            for (float top = 0; top < s.Height; top += sliceH)
            {
                using var c = doc.BeginPage(pageW, pageH);
                c.Clear(SKColors.White);
                c.Translate(margin, margin);
                c.Scale(scale);
                c.ClipRect(SKRect.Create(0, 0, s.Width, Math.Min(sliceH, s.Height - top)));
                c.DrawBitmap(s, 0, -top);
                doc.EndPage();
            }
        }
        doc.Close();
    }

    public void Dispose() => _k.Dispose();
}
