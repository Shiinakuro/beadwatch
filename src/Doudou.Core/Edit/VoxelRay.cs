using System.Numerics;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Edit;

/// <summary>射线拾取结果。Cell = 命中的豆；Normal = 命中面的朝外方向；OnGround = 打在地面网格上（模型外）。</summary>
public readonly record struct RayHit(Int3 Cell, Int3 Normal, bool OnGround, float Distance)
{
    /// <summary>「贴附」时新豆应放的位置。</summary>
    public Int3 AttachCell => OnGround ? Cell : Cell + Normal;
}

public static class VoxelRay
{
    /// <summary>
    /// 体素 DDA（Amanatides-Woo）。visible 为 null 时所有豆可见；否则只命中 visible(index)==true 的豆。
    /// 打不中豆时，若射线与地面 y=0 平面相交且落在网格范围内，返回地面命中。
    /// </summary>
    public static RayHit? Cast(VoxelModel m, Vector3 origin, Vector3 dir, Func<int, bool>? visible = null, bool groundFallback = true)
    {
        dir = Vector3.Normalize(dir);
        // 与网格包围盒求交
        var bmin = Vector3.Zero; var bmax = new Vector3(m.SX, m.SY, m.SZ);
        float tmin = 0, tmax = float.MaxValue;
        int enterAxis = -1;
        for (int a = 0; a < 3; a++)
        {
            float o = a == 0 ? origin.X : a == 1 ? origin.Y : origin.Z;
            float d = a == 0 ? dir.X : a == 1 ? dir.Y : dir.Z;
            float lo = a == 0 ? bmin.X : a == 1 ? bmin.Y : bmin.Z;
            float hi = a == 0 ? bmax.X : a == 1 ? bmax.Y : bmax.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) return groundFallback ? Ground(m, origin, dir) : null;
                continue;
            }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tmin) { tmin = t1; enterAxis = a; }
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return groundFallback ? Ground(m, origin, dir) : null;
        }
        var p = origin + dir * (tmin + 1e-4f);
        int x = Math.Clamp((int)MathF.Floor(p.X), 0, m.SX - 1);
        int y = Math.Clamp((int)MathF.Floor(p.Y), 0, m.SY - 1);
        int z = Math.Clamp((int)MathF.Floor(p.Z), 0, m.SZ - 1);
        int stepX = dir.X > 0 ? 1 : -1, stepY = dir.Y > 0 ? 1 : -1, stepZ = dir.Z > 0 ? 1 : -1;
        float tdx = MathF.Abs(1 / dir.X), tdy = MathF.Abs(1 / dir.Y), tdz = MathF.Abs(1 / dir.Z);
        float tmx = dir.X > 0 ? (x + 1 - origin.X) / dir.X : (x - origin.X) / dir.X;
        float tmy = dir.Y > 0 ? (y + 1 - origin.Y) / dir.Y : (y - origin.Y) / dir.Y;
        float tmz = dir.Z > 0 ? (z + 1 - origin.Z) / dir.Z : (z - origin.Z) / dir.Z;
        // 某分量为 0 时该轴永远不跨格
        if (dir.X == 0 || !float.IsFinite(tmx)) { tmx = float.MaxValue; tdx = float.MaxValue; }
        if (dir.Y == 0 || !float.IsFinite(tmy)) { tmy = float.MaxValue; tdy = float.MaxValue; }
        if (dir.Z == 0 || !float.IsFinite(tmz)) { tmz = float.MaxValue; tdz = float.MaxValue; }
        int lastAxis = enterAxis;
        float t = tmin;
        while (true)
        {
            int i = m.Index(x, y, z);
            if (m.Colors[i] != 0 && (visible == null || visible(i)))
            {
                var n = lastAxis switch
                {
                    0 => new Int3(-stepX, 0, 0),
                    1 => new Int3(0, -stepY, 0),
                    2 => new Int3(0, 0, -stepZ),
                    _ => new Int3(0, 1, 0),
                };
                return new RayHit(new Int3(x, y, z), n, false, t);
            }
            if (tmx < tmy && tmx < tmz) { x += stepX; t = tmx; tmx += tdx; lastAxis = 0; if ((uint)x >= (uint)m.SX) break; }
            else if (tmy < tmz) { y += stepY; t = tmy; tmy += tdy; lastAxis = 1; if ((uint)y >= (uint)m.SY) break; }
            else { z += stepZ; t = tmz; tmz += tdz; lastAxis = 2; if ((uint)z >= (uint)m.SZ) break; }
        }
        return groundFallback ? Ground(m, origin, dir) : null;
    }

    private static RayHit? Ground(VoxelModel m, Vector3 origin, Vector3 dir)
    {
        if (MathF.Abs(dir.Y) < 1e-6f) return null;
        float t = -origin.Y / dir.Y;
        if (t <= 0) return null;
        var p = origin + dir * t;
        int x = (int)MathF.Floor(p.X), z = (int)MathF.Floor(p.Z);
        if ((uint)x >= (uint)m.SX || (uint)z >= (uint)m.SZ) return null;
        return new RayHit(new Int3(x, 0, z), new Int3(0, 1, 0), true, t);
    }

    /// <summary>射线与某个轴对齐平面的交点（框选、拖拽矩形用）。</summary>
    public static Vector3? PlaneHit(Vector3 origin, Vector3 dir, int axis, float coord)
    {
        float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
        float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
        if (MathF.Abs(d) < 1e-6f) return null;
        float t = (coord - o) / d;
        return t <= 0 ? null : origin + dir * t;
    }
}
