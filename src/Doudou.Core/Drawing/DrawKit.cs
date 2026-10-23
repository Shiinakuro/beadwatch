using Doudou.Core.Color;
using Doudou.Core.Model;
using SkiaSharp;

namespace Doudou.Core.Drawing;

/// <summary>图纸绘制的公共资源：字体、配色、色号字母表。</summary>
public sealed class DrawKit : IDisposable
{
    public SKTypeface Regular { get; }
    public SKTypeface Bold { get; }
    public SKTypeface Heavy { get; }

    public SKColor Paper = new(0xEE, 0xF2, 0xEA);
    public SKColor Card = SKColors.White;
    public SKColor Ink = new(0x2B, 0x33, 0x3B);
    public SKColor Muted = new(0x7A, 0x84, 0x8F);
    public SKColor Accent = new(0xE8, 0x5D, 0x3F);
    public SKColor Blue = new(0x2F, 0x6F, 0xE4);
    public SKColor GridLine = new(0xC9, 0xCE, 0xD6);

    public DrawKit()
    {
        Regular = Pick(SKFontStyle.Normal, "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC");
        Bold = Pick(SKFontStyle.Bold, "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC");
        Heavy = Pick(new SKFontStyle(SKFontStyleWeight.Black, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
            "Microsoft YaHei UI", "Microsoft YaHei", "SimHei");
    }

    private static SKTypeface Pick(SKFontStyle style, params string[] families)
    {
        foreach (var f in families)
        {
            var t = SKTypeface.FromFamilyName(f, style);
            if (t != null && t.FamilyName.Equals(f, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return SKFontManager.Default.MatchCharacter('豆') ?? SKTypeface.Default;
    }

    public SKPaint Text(float size, SKColor? color = null, bool bold = false, SKTextAlign align = SKTextAlign.Left, SKTypeface? face = null) => new()
    {
        IsAntialias = true,
        TextSize = size,
        Color = color ?? Ink,
        Typeface = face ?? (bold ? Bold : Regular),
        TextAlign = align,
        SubpixelText = true,
    };

    public static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c };
    public static SKPaint Stroke(SKColor c, float w) => new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = c, StrokeWidth = w };

    public static SKColor Sk(Rgb c, byte a = 255) => new(c.R, c.G, c.B, a);

    /// <summary>深色底配白字，浅色底配黑字。</summary>
    public static SKColor LabelOn(Rgb bg) => bg.Luma < 140 ? SKColors.White : new SKColor(0x22, 0x22, 0x22);

    public void DrawCard(SKCanvas c, SKRect r, string? title = null, SKColor? fill = null)
    {
        using var shadow = Fill(new SKColor(0, 0, 0, 22));
        c.DrawRoundRect(new SKRect(r.Left + 4, r.Top + 6, r.Right + 4, r.Bottom + 6), 28, 28, shadow);
        using var f = Fill(fill ?? Card);
        c.DrawRoundRect(r, 28, 28, f);
        if (title != null)
        {
            using var tp = Text(46, Ink, true);
            c.DrawText(title, r.Left + 48, r.Top + 76, tp);
            using var bar = Fill(Accent);
            c.DrawRoundRect(new SKRect(r.Left + 28, r.Top + 38, r.Left + 38, r.Top + 84), 4, 4, bar);
        }
    }

    public void Dispose()
    {
        Regular.Dispose(); Bold.Dispose(); Heavy.Dispose();
    }
}

/// <summary>颜色在图纸中的标注：A、B、C…（按用量从多到少），任意色标「任」。</summary>
public sealed class ColorLegend
{
    public sealed class Entry
    {
        public ushort Index;
        public string Label = "";
        public BeadColor Color = null!;
        public int Count;
        public Dictionary<string, string> BrandCodes = new();
    }

    public List<Entry> Entries { get; } = new();
    private readonly Dictionary<ushort, Entry> _byIndex = new();

    public Entry? Of(ushort idx) => _byIndex.TryGetValue(idx, out var e) ? e : null;
    public int Total => Entries.Sum(e => e.Count);

    public static ColorLegend Build(VoxelModel m, PaletteLibrary lib)
    {
        var lg = new ColorLegend();
        var counts = new Dictionary<ushort, int>();
        foreach (var c in m.Colors) if (c != 0) counts[c] = counts.GetValueOrDefault(c) + 1;
        int k = 0;
        foreach (var (idx, cnt) in counts.OrderBy(kv => m.Palette[kv.Key].IsAny ? 1 : 0).ThenByDescending(kv => kv.Value))
        {
            var bc = m.Palette[idx];
            var e = new Entry { Index = idx, Color = bc, Count = cnt, Label = bc.IsAny ? "任" : LetterCode(k++) };
            foreach (var (bid, label) in PaletteLibrary.DrawingBrands)
            {
                if (bc.IsAny) { e.BrandCodes[label] = "任意色"; continue; }
                if (!lib.Brands.ContainsKey(bid)) continue;
                e.BrandCodes[label] = lib.Convert(m.BrandId, bc.Code, bc.Rgb, bid).Code;
            }
            lg.Entries.Add(e);
            lg._byIndex[idx] = e;
        }
        return lg;
    }

    public static string LetterCode(int k)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return k < 26 ? A[k].ToString() : A[k / 26 - 1].ToString() + A[k % 26];
    }
}
