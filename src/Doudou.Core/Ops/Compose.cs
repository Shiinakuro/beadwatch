using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.Ops;

/// <summary>多个模型组合（例如风铃 + 下方吊着的小鱼）。</summary>
public static class Compose
{
    /// <summary>
    /// 把 accessory 挂在 main 正下方：用一列 stringRgb 色的豆当挂绳，长度 stringLen。
    /// 挂绳从 main 底部中心向下，落到 accessory 质心所在列的最高点。
    /// </summary>
    public static VoxelModel HangBelow(VoxelModel main, VoxelModel accessory, int stringLen, string stringCode, Rgb stringRgb)
    {
        var mb = main.Bounds() ?? throw new InvalidOperationException("主体为空");
        var ab = accessory.Bounds() ?? throw new InvalidOperationException("挂件为空");
        // 主体底部中心列
        int cx = (mb.Min.X + mb.Max.X) / 2, cz = (mb.Min.Z + mb.Max.Z) / 2;
        int bottomY = mb.Min.Y;
        for (int y = mb.Min.Y; y <= mb.Max.Y; y++) if (main.Solid(cx, y, cz)) { bottomY = y; break; }
        // 挂件：质心列的最高豆
        double sx = 0, sz = 0; int n = 0;
        for (int i = 0; i < accessory.Count; i++)
            if (accessory.Colors[i] != 0) { var p = accessory.Pos(i); sx += p.X; sz += p.Z; n++; }
        int ax = (int)Math.Round(sx / n), az = (int)Math.Round(sz / n);
        int topY = -1;
        for (int y = accessory.SY - 1; y >= 0 && topY < 0; y--) if (accessory.Solid(ax, y, az)) topY = y;
        if (topY < 0) { ax = (ab.Min.X + ab.Max.X) / 2; az = (ab.Min.Z + ab.Max.Z) / 2; topY = ab.Max.Y; }

        // 挂件在新坐标系中的偏移：其 (ax, topY, az) 落到 (cx, bottomY - stringLen - 1, cz)
        int offX = cx - ax, offZ = cz - az;
        int accTopNew = bottomY - stringLen - 1;
        int offY = accTopNew - topY;
        int minX = Math.Min(0, ab.Min.X + offX), maxX = Math.Max(main.SX - 1, ab.Max.X + offX);
        int minY = Math.Min(0, ab.Min.Y + offY), maxY = main.SY - 1;
        int minZ = Math.Min(0, ab.Min.Z + offZ), maxZ = Math.Max(main.SZ - 1, ab.Max.Z + offZ);
        var shift = new Int3(-minX, -minY, -minZ);
        var r = main.Resized(maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1, shift);
        for (int i = 0; i < accessory.Count; i++)
        {
            var c = accessory.Colors[i];
            if (c == 0) continue;
            var bc = accessory.Palette[c];
            var idx = r.EnsureColor(bc.Code, bc.Rgb, bc.IsAny);
            var q = accessory.Pos(i) + new Int3(offX, offY, offZ) + shift;
            if (r.InBounds(q)) r.Set(q, idx);
        }
        var sIdx = r.EnsureColor(stringCode, stringRgb);
        for (int k = 1; k <= stringLen; k++)
        {
            var q = new Int3(cx, bottomY - k, cz) + shift;
            if (r.InBounds(q) && !r.Solid(q)) r.Set(q, sIdx);
        }
        r.Touch();
        return r;
    }
}
