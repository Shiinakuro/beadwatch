using System.Text;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;

namespace Doudou.Core.IO;

/// <summary>
/// MagicaVoxel .vox 读写。MagicaVoxel 是 Z 朝上，本程序是 Y 朝上：
///   本程序 (x, y, z)  ↔  vox (x, sizeZ-1-z, y)
/// 读入时多个模型（场景）只取第一个；颜色先作为「原始色」放进调色板，之后可再映射到品牌色卡。
/// </summary>
public static class VoxFile
{
    public static VoxelModel Read(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "VOX ") throw new InvalidDataException("不是 .vox 文件");
        br.ReadInt32(); // version
        int sx = 0, sy = 0, sz = 0;
        var voxels = new List<(byte X, byte Y, byte Z, byte C)>();
        uint[]? palette = null;
        bool gotModel = false;

        void ReadChunk(long end)
        {
            while (br.BaseStream.Position < end)
            {
                string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                int content = br.ReadInt32();
                int children = br.ReadInt32();
                long next = br.BaseStream.Position + content;
                switch (id)
                {
                    case "SIZE" when !gotModel:
                        sx = br.ReadInt32(); sy = br.ReadInt32(); sz = br.ReadInt32();
                        break;
                    case "XYZI" when !gotModel:
                        int n = br.ReadInt32();
                        for (int i = 0; i < n; i++)
                            voxels.Add((br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte()));
                        gotModel = true;
                        break;
                    case "RGBA":
                        palette = new uint[256];
                        for (int i = 0; i < 256; i++) palette[i] = br.ReadUInt32();
                        break;
                }
                br.BaseStream.Position = next;
                if (id == "MAIN") { ReadChunk(next + children); }
                else br.BaseStream.Position = next + children;
            }
        }
        ReadChunk(br.BaseStream.Length);
        if (!gotModel) throw new InvalidDataException(".vox 中没有模型");
        palette ??= DefaultPalette();

        // vox(x, y, z) Z-up → 本程序 (x, z, sy-1-y)：vox 的 -Y 是正面
        var m = new VoxelModel(sx, sz, sy) { Name = Path.GetFileNameWithoutExtension(path) };
        var map = new Dictionary<byte, ushort>();
        foreach (var (x, y, z, c) in voxels)
        {
            if (!map.TryGetValue(c, out var idx))
            {
                uint p = palette[(c - 1) & 0xFF];
                var rgb = new Rgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF));
                idx = m.EnsureColor("V" + c, rgb);
                map[c] = idx;
            }
            m.Set(new Int3(x, z, sy - 1 - y), idx);
        }
        m.Touch();
        return m;
    }

    public static void Write(VoxelModel m, string path)
    {
        if (m.SX > 256 || m.SY > 256 || m.SZ > 256) throw new InvalidOperationException(".vox 单模型最大 256³");
        // 最多 255 色：超出时按出现频次截断（极少见）
        var used = new Dictionary<ushort, int>();
        foreach (var c in m.Colors) if (c != 0) used[c] = used.GetValueOrDefault(c) + 1;
        var order = used.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(255).ToList();
        var toVox = new Dictionary<ushort, byte>();
        for (int i = 0; i < order.Count; i++) toVox[order[i]] = (byte)(i + 1);

        var xyzi = new MemoryStream();
        var w = new BinaryWriter(xyzi);
        int count = 0;
        var body = new MemoryStream(); var bw = new BinaryWriter(body);
        for (int i = 0; i < m.Count; i++)
        {
            var c = m.Colors[i];
            if (c == 0 || !toVox.TryGetValue(c, out var vc)) continue;
            var p = m.Pos(i);
            bw.Write((byte)p.X); bw.Write((byte)(m.SZ - 1 - p.Z)); bw.Write((byte)p.Y); bw.Write(vc);
            count++;
        }
        w.Write(count); w.Write(body.ToArray());

        var rgba = new MemoryStream(); var rw = new BinaryWriter(rgba);
        for (int i = 0; i < 256; i++)
        {
            uint v = 0xFF000000;
            if (i < order.Count)
            {
                var rgb = m.RgbOf(order[i]);
                v = 0xFF000000u | ((uint)rgb.B << 16) | ((uint)rgb.G << 8) | rgb.R;
            }
            rw.Write(v);
        }

        using var f = new BinaryWriter(File.Create(path));
        byte[] Chunk(string id, byte[] content)
        {
            var ms = new MemoryStream(); var cw = new BinaryWriter(ms);
            cw.Write(Encoding.ASCII.GetBytes(id)); cw.Write(content.Length); cw.Write(0); cw.Write(content);
            return ms.ToArray();
        }
        var size = new MemoryStream(); var sw = new BinaryWriter(size);
        sw.Write(m.SX); sw.Write(m.SZ); sw.Write(m.SY);
        var children = Chunk("SIZE", size.ToArray()).Concat(Chunk("XYZI", xyzi.ToArray())).Concat(Chunk("RGBA", rgba.ToArray())).ToArray();
        f.Write(Encoding.ASCII.GetBytes("VOX ")); f.Write(150);
        f.Write(Encoding.ASCII.GetBytes("MAIN")); f.Write(0); f.Write(children.Length); f.Write(children);
    }

    private static uint[] DefaultPalette()
    {
        // MagicaVoxel 默认调色板近似：6×6×6 色立方 + 灰阶
        var p = new uint[256];
        int k = 0;
        for (int r = 5; r >= 0 && k < 216; r--)
            for (int g = 5; g >= 0 && k < 216; g--)
                for (int b = 5; b >= 0 && k < 216; b--)
                    p[k++] = 0xFF000000u | ((uint)(b * 51) << 16) | ((uint)(g * 51) << 8) | (uint)(r * 51);
        for (; k < 256; k++) { uint v = (uint)((255 - k) * 6 % 256); p[k] = 0xFF000000u | (v << 16) | (v << 8) | v; }
        return p;
    }
}
