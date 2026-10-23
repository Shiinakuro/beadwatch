using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Core.Parts;
using SkiaSharp;

namespace Doudou.Core.Render;

public sealed class AssemblyAnimationOptions
{
    /// <summary>每帧最长边（像素）。动图不宜太大，否则文件臃肿。</summary>
    public int FitSize { get; set; } = 560;
    public float Yaw { get; set; } = 35;
    public float Pitch { get; set; } = 24;
    /// <summary>每个零件「滑入」的子帧数（1 = 直接出现，不滑动）。</summary>
    public int SubFrames { get; set; } = 4;
    /// <summary>滑入起点相对成品的偏移（颗）。</summary>
    public float SlideDistance { get; set; } = 6;
    /// <summary>每步到位后额外停顿帧数（含到位那一帧）。</summary>
    public int HoldFrames { get; set; } = 2;
    public bool Bead { get; set; } = true;
    public SKColor Background { get; set; } = new(0xF6, 0xF7, 0xF4);
    /// <summary>零件很多时自动退化为「一步一帧」的阈值。</summary>
    public int MaxSmoothSteps { get; set; } = 26;
}

/// <summary>
/// 装配动图：按 AssemblyPlanner 已验证的装配顺序，逐帧把零件「装」到成品上，
/// 当前件沿其装入方向的反方向滑入、其余件半灰，得到一段可循环的拼装演示。
/// 每帧取景完全一致（固定相机），可直接合成 GIF 或存成 PNG 序列。
/// </summary>
public static class AssemblyAnimation
{
    public static List<SKBitmap> BuildFrames(VoxelModel m, ModelPartsReport rep, AssemblyPlan plan, AssemblyAnimationOptions? o = null)
    {
        o ??= new AssemblyAnimationOptions();
        var frames = new List<SKBitmap>();
        var parts = rep.Parts.Where(p => p.Beads > 0).ToList();
        if (parts.Count == 0 || plan.Steps.Count == 0) return frames;

        // 用完整模型定一次取景，之后每帧都套这个固定框，保证相机与画布完全一致
        var full = IsoRenderer.FromModel(m).ToList();
        var frame = IsoRenderer.Bounds(full, o.Yaw, o.Pitch);
        // 滑入的零件中途在成品外面：取景框要连滑入起点一起框住，否则动画里零件会被画布边切掉
        if (o.SubFrames > 1 && o.SlideDistance > 0)
        {
            var (right, up, _) = IsoRenderer.Camera(o.Yaw, o.Pitch);
            foreach (var st in plan.Steps)
            {
                if (st.InsertDir < 0) continue;
                var off = ToVec(Dir6.All[st.InsertDir]) * o.SlideDistance;
                float dx = Vector3.Dot(off, right), dy = -Vector3.Dot(off, up);
                frame = (Math.Min(frame.MinX, frame.MinX + dx), Math.Min(frame.MinY, frame.MinY + dy),
                         Math.Max(frame.MaxX, frame.MaxX + dx), Math.Max(frame.MaxY, frame.MaxY + dy));
            }
        }

        bool smooth = plan.Steps.Count <= o.MaxSmoothSteps;
        int sub = smooth ? Math.Max(1, o.SubFrames) : 1;
        int hold = smooth ? Math.Max(1, o.HoldFrames) : 1;

        var placed = new HashSet<int>();
        foreach (var st in plan.Steps)
        {
            placed.Add(st.PartId);
            var dir = st.InsertDir >= 0 ? ToVec(Dir6.All[st.InsertDir]) : Vector3.Zero;
            for (int f = 0; f < sub; f++)
            {
                float t = sub == 1 ? 0f : (float)(sub - 1 - f) / (sub - 1);   // 1 → 0
                frames.Add(RenderFrame(m, rep, placed, st.PartId, dir * (o.SlideDistance * t), o, frame));
            }
            for (int hh = 1; hh < hold; hh++)
                frames.Add(RenderFrame(m, rep, placed, st.PartId, Vector3.Zero, o, frame));
        }
        return frames;
    }

    private static SKBitmap RenderFrame(VoxelModel m, ModelPartsReport rep, HashSet<int> placed, int curId,
        Vector3 curOff, AssemblyAnimationOptions o, (float MinX, float MinY, float MaxX, float MaxY) frame)
    {
        var beads = new List<RenderBead>();
        foreach (var pr in rep.Parts)
        {
            if (pr.Beads == 0 || !placed.Contains(pr.Id)) continue;
            bool cur = pr.Id == curId;
            foreach (var i in pr.Cells)
            {
                var col = m.RgbOf(m.Colors[i]);
                if (!cur) col = Rgb.Lerp(col, new Rgb(224, 227, 230), 0.5);
                beads.Add(new RenderBead
                {
                    Cell = m.Pos(i),
                    Offset = cur ? curOff : Vector3.Zero,
                    Color = col,
                    Alpha = 255,
                    Group = pr.Id,
                });
            }
        }
        using var r = IsoRenderer.Render(beads, new RenderOptions
        {
            Yaw = o.Yaw, Pitch = o.Pitch, FitSize = o.FitSize, Margin = 18,
            Look = o.Bead ? BeadLook.Bead : BeadLook.Cube,
            Background = o.Background,
            FixedFrame = frame,
        });
        return r.Bitmap.Copy();
    }

    private static Vector3 ToVec(Int3 d) => new(d.X, d.Y, d.Z);
}
