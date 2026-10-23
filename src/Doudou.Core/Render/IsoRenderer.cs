using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using SkiaSharp;

namespace Doudou.Core.Render;

/// <summary>要画的一颗豆。Group 相同的豆之间做内面剔除（爆炸图里每个零件一组）。</summary>
public struct RenderBead
{
    public Int3 Cell;
    public Vector3 Offset;
    public Rgb Color;
    public byte Alpha;
    public int Group;
    public bool Highlight;
}

public enum BeadLook { Cube, Bead }

public sealed class RenderOptions
{
    /// <summary>水平转角（度）。0 = 正对正面，45 = 经典等轴测。</summary>
    public float Yaw { get; set; } = 35;
    /// <summary>俯角（度）。</summary>
    public float Pitch { get; set; } = 28;
    public float PixelsPerVoxel { get; set; } = 12;
    /// <summary>若 > 0：自动缩放到这个最大边长（像素）。</summary>
    public int FitSize { get; set; }
    public int Margin { get; set; } = 20;
    public BeadLook Look { get; set; } = BeadLook.Cube;
    public bool Outline { get; set; } = true;
    public SKColor Background { get; set; } = SKColors.Transparent;
    public Vector3 Light { get; set; } = Vector3.Normalize(new Vector3(-0.45f, 0.85f, 0.55f));
    public bool Orthographic2D { get; set; }
    /// <summary>固定的投影包围盒：设置后画布尺寸与取景不再随本帧内容变化，多帧之间相机完全一致（做装配动图用）。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY)? FixedFrame { get; set; }
}

/// <summary>投影结果：位图 + 3D→2D 映射（给引线标签用）。</summary>
public sealed class RenderResult : IDisposable
{
    public SKBitmap Bitmap { get; init; } = null!;
    public Func<Vector3, SKPoint> Project { get; init; } = null!;
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// 软件等轴测渲染器（Skia）：面剔除 + 画家算法 + 分面明暗 + 豆格描边。
/// 图纸里的整体效果图、爆炸图、装配步骤图都用它出。
/// </summary>
public static class IsoRenderer
{
    private static readonly Vector3[] FaceNormals =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    // 每个面 4 个角（单位立方体内，逆时针朝外）
    private static readonly Vector3[][] FaceCorners =
    {
        new Vector3[] { new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1) },
        new Vector3[] { new(0, 0, 1), new(0, 1, 1), new(0, 1, 0), new(0, 0, 0) },
        new Vector3[] { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) },
        new Vector3[] { new(1, 0, 0), new(0, 0, 0), new(0, 1, 0), new(1, 1, 0) },
    };

    public static (Vector3 Right, Vector3 Up, Vector3 Forward) Camera(float yawDeg, float pitchDeg)
    {
        float yaw = yawDeg * MathF.PI / 180, pitch = pitchDeg * MathF.PI / 180;
        // 相机位于物体前上方，朝向物体：forward 指向场景内部
        var toCam = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        var forward = -toCam;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        return (right, up, forward);
    }

    /// <summary>一组豆在指定相机下的投影包围盒（用于固定动画每帧取景）。</summary>
    public static (float MinX, float MinY, float MaxX, float MaxY) Bounds(IReadOnlyList<RenderBead> beads, float yawDeg, float pitchDeg)
    {
        var (right, up, _) = Camera(yawDeg, pitchDeg);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var b in beads)
        {
            var c = new Vector3(b.Cell.X, b.Cell.Y, b.Cell.Z) + b.Offset;
            for (int k = 0; k < 8; k++)
            {
                var p = c + new Vector3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
                float sx = Vector3.Dot(p, right), sy = -Vector3.Dot(p, up);
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }
        }
        if (beads.Count == 0) { minX = minY = 0; maxX = maxY = 1; }
        return (minX, minY, maxX, maxY);
    }

    public static IEnumerable<RenderBead> FromModel(VoxelModel m, Func<int, bool>? filter = null, bool tintParts = false)
    {
        for (int i = 0; i < m.Count; i++)
        {
            var c = m.Colors[i];
            if (c == 0) continue;
            if (filter != null && !filter(i)) continue;
            var rgb = m.RgbOf(c);
            if (tintParts && m.Parts[i] != 0)
            {
                var p = m.FindPart(m.Parts[i]);
                if (p != null) rgb = p.Tint;
            }
            yield return new RenderBead { Cell = m.Pos(i), Color = rgb, Alpha = 255, Group = 0 };
        }
    }

    public static RenderResult Render(IReadOnlyList<RenderBead> beads, RenderOptions o)
    {
        var (right, up, forward) = Camera(o.Yaw, o.Pitch);
        var toCam = -forward;

        // 同组占用表用于剔除内面
        var occupied = new HashSet<(int, Int3)>();
        foreach (var b in beads) if (b.Alpha == 255) occupied.Add((b.Group, b.Cell));

        var faces = new List<(float Depth, int Bead, int Face)>(beads.Count * 3);
        for (int i = 0; i < beads.Count; i++)
        {
            var b = beads[i];
            for (int f = 0; f < 6; f++)
            {
                if (Vector3.Dot(FaceNormals[f], toCam) <= 1e-4f) continue;
                var n = b.Cell + Dir6.All[f];
                if (b.Alpha == 255 && occupied.Contains((b.Group, n))) continue;
                var center = new Vector3(b.Cell.X + 0.5f, b.Cell.Y + 0.5f, b.Cell.Z + 0.5f) + b.Offset + FaceNormals[f] * 0.5f;
                faces.Add((Vector3.Dot(center, forward), i, f));
            }
        }
        faces.Sort((a, b) => b.Depth.CompareTo(a.Depth));   // 远 → 近

        // 投影范围
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        if (o.FixedFrame is { } ff)
        {
            minX = ff.MinX; minY = ff.MinY; maxX = ff.MaxX; maxY = ff.MaxY;
        }
        else
        {
            foreach (var b in beads)
            {
                var c = new Vector3(b.Cell.X, b.Cell.Y, b.Cell.Z) + b.Offset;
                for (int k = 0; k < 8; k++)
                {
                    var p = c + new Vector3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
                    float sx = Vector3.Dot(p, right), sy = -Vector3.Dot(p, up);
                    minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                    minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
                }
            }
            if (beads.Count == 0) { minX = minY = 0; maxX = maxY = 1; }
        }
        float ppv = o.PixelsPerVoxel;
        if (o.FitSize > 0)
            ppv = Math.Max(0.5f, (o.FitSize - 2 * o.Margin) / Math.Max(maxX - minX, maxY - minY));
        int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) * ppv) + 2 * o.Margin);
        int h = Math.Max(1, (int)Math.Ceiling((maxY - minY) * ppv) + 2 * o.Margin);
        float ox = o.Margin - minX * ppv, oy = o.Margin - minY * ppv;
        SKPoint Proj(Vector3 p) => new(ox + Vector3.Dot(p, right) * ppv, oy - Vector3.Dot(p, up) * ppv);

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(o.Background);
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Clamp(ppv * 0.06f, 0.6f, 2f) };
            using var hole = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var path = new SKPath();
            bool drawEdges = o.Outline && ppv >= 3;
            foreach (var (_, bi, f) in faces)
            {
                var b = beads[bi];
                var basePos = new Vector3(b.Cell.X, b.Cell.Y, b.Cell.Z) + b.Offset;
                var n = FaceNormals[f];
                float lambert = 0.72f + 0.28f * Math.Max(0, Vector3.Dot(n, o.Light));
                var col = b.Color.Scale(lambert);
                if (b.Highlight) col = Rgb.Lerp(col, new Rgb(255, 80, 60), 0.35);
                fill.Color = new SKColor(col.R, col.G, col.B, b.Alpha);
                path.Reset();
                var cs = FaceCorners[f];
                path.MoveTo(Proj(basePos + cs[0]));
                for (int k = 1; k < 4; k++) path.LineTo(Proj(basePos + cs[k]));
                path.Close();
                canvas.DrawPath(path, fill);
                if (drawEdges)
                {
                    var ec = col.Scale(0.62);
                    edge.Color = new SKColor(ec.R, ec.G, ec.B, b.Alpha);
                    canvas.DrawPath(path, edge);
                }
                if (o.Look == BeadLook.Bead && ppv >= 6)
                {
                    // 豆子的孔：面中心画一个按投影压扁的小圆
                    var center = basePos + (cs[0] + cs[2]) / 2;
                    var a = Proj(center + (cs[1] - cs[0]) * 0.17f) - Proj(center);
                    var c2 = Proj(center + (cs[3] - cs[0]) * 0.17f) - Proj(center);
                    var hc = col.Scale(0.55);
                    hole.Color = new SKColor(hc.R, hc.G, hc.B, b.Alpha);
                    using var hp = new SKPath();
                    var pc = Proj(center);
                    for (int k = 0; k <= 16; k++)
                    {
                        double t = k * Math.PI * 2 / 16;
                        var pt = new SKPoint(pc.X + a.X * (float)Math.Cos(t) + c2.X * (float)Math.Sin(t), pc.Y + a.Y * (float)Math.Cos(t) + c2.Y * (float)Math.Sin(t));
                        if (k == 0) hp.MoveTo(pt); else hp.LineTo(pt);
                    }
                    canvas.DrawPath(hp, hole);
                }
            }
        }
        return new RenderResult { Bitmap = bmp, Project = Proj };
    }

    public static void SavePng(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }
}
