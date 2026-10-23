using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Core.Parts;
using Doudou.Core.Render;

namespace Doudou.Editor;

/// <summary>体素 → 可见面网格（每顶点 12 个 float：位置3 法线3 颜色3 UV2 标志1）。</summary>
public static class MeshBuilder
{
    public const int Stride = 12;

    [Flags]
    public enum F { None = 0, Selected = 1, Highlight = 2, AnyColor = 4, Unassigned = 8, Ghost = 16 }

    private static readonly Vector3[] N =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    private static readonly Vector3[][] C =
    {
        new Vector3[] { new(1, 0, 1), new(1, 0, 0), new(1, 1, 0), new(1, 1, 1) },
        new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0) },
        new Vector3[] { new(0, 1, 1), new(1, 1, 1), new(1, 1, 0), new(0, 1, 0) },
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) },
        new Vector3[] { new(1, 0, 0), new(0, 0, 0), new(0, 1, 0), new(1, 1, 0) },
    };

    public sealed class Mesh
    {
        public float[] Vertices = Array.Empty<float>();
        public uint[] Indices = Array.Empty<uint>();
        public int FaceCount;
        /// <summary>零件爆炸偏移（拾取时需要反算）。</summary>
        public Dictionary<int, Vector3> Offsets = new();
    }

    public static Mesh Build(Session s)
    {
        var m = s.Model;
        var verts = new List<float>(m.Count);
        var idx = new List<uint>(m.Count);
        var mesh = new Mesh();
        bool exploded = s.Explode > 1.01f && m.PartList.Count > 0;
        if (exploded)
        {
            var rep = PartAnalyzer.Analyze(m, writeBackAxis: false);
            var lay = Exploded.Build(m, rep, s.Explode, minGap: 1.5f);
            mesh.Offsets = lay.Offsets;
        }
        var visible = new bool[m.Count];
        for (int i = 0; i < m.Count; i++) visible[i] = s.IsVisible(i);

        for (int i = 0; i < m.Count; i++)
        {
            if (!visible[i]) continue;
            var p = m.Pos(i);
            int pid = m.Parts[i];
            var bead = m.Palette[m.Colors[i]];
            Rgb rgb;
            var flags = F.None;
            if (s.Display == DisplayMode.PartTint)
            {
                var part = pid != 0 ? m.FindPart(pid) : null;
                rgb = part?.Tint ?? new Rgb(120, 124, 130);
                if (pid == 0) flags |= F.Unassigned;
                if (pid != 0 && pid == s.CurrentPart) rgb = Rgb.Lerp(rgb, new Rgb(255, 255, 255), 0.25);
            }
            else
            {
                rgb = bead.IsAny ? new Rgb(64, 74, 70) : bead.Rgb;
            }
            if (bead.IsAny) flags |= F.AnyColor;
            if (s.Selection.Contains(i)) flags |= F.Selected;
            if (s.Highlight.Contains(i)) flags |= F.Highlight;
            var off = exploded && mesh.Offsets.TryGetValue(pid, out var o) ? o : Vector3.Zero;

            for (int f = 0; f < 6; f++)
            {
                var q = p + Dir6.All[f];
                if (m.InBounds(q))
                {
                    int j = m.Index(q);
                    if (visible[j] && (!exploded || m.Parts[j] == pid)) continue;
                }
                uint b = (uint)(verts.Count / Stride);
                var n = N[f];
                for (int k = 0; k < 4; k++)
                {
                    var v = new Vector3(p.X, p.Y, p.Z) + C[f][k] + off;
                    verts.Add(v.X); verts.Add(v.Y); verts.Add(v.Z);
                    verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                    verts.Add(rgb.R / 255f); verts.Add(rgb.G / 255f); verts.Add(rgb.B / 255f);
                    verts.Add(k == 0 || k == 3 ? 0 : 1); verts.Add(k < 2 ? 0 : 1);
                    verts.Add((float)flags);
                }
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
                idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
                mesh.FaceCount++;
            }
        }
        mesh.Vertices = verts.ToArray();
        mesh.Indices = idx.ToArray();
        return mesh;
    }

    /// <summary>预览方块（半透明）。</summary>
    public static Mesh Cubes(IEnumerable<Int3> cells, Rgb color)
    {
        var verts = new List<float>();
        var idx = new List<uint>();
        int faces = 0;
        foreach (var p in cells)
            for (int f = 0; f < 6; f++)
            {
                uint b = (uint)(verts.Count / Stride);
                var n = N[f];
                for (int k = 0; k < 4; k++)
                {
                    // 稍微外扩，避免与已有豆的表面 Z 冲突
                    var v = new Vector3(p.X, p.Y, p.Z) + (C[f][k] - new Vector3(0.5f)) * 1.02f + new Vector3(0.5f);
                    verts.Add(v.X); verts.Add(v.Y); verts.Add(v.Z);
                    verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                    verts.Add(color.R / 255f); verts.Add(color.G / 255f); verts.Add(color.B / 255f);
                    verts.Add(k == 0 || k == 3 ? 0 : 1); verts.Add(k < 2 ? 0 : 1);
                    verts.Add(0);
                }
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
                idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
                faces++;
            }
        return new Mesh { Vertices = verts.ToArray(), Indices = idx.ToArray(), FaceCount = faces };
    }
}
