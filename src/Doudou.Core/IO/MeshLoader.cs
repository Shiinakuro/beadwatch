using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Doudou.Core.IO;

/// <summary>三角网格（只关心几何，可带顶点色）。</summary>
public sealed class TriMesh
{
    public List<Vector3> Vertices { get; } = new();
    public List<Vector3>? VertexColors { get; set; }   // 0..1
    /// <summary>每个顶点对应的 UV（GLB/OBJ 纹理）。</summary>
    public List<Vector2>? VertexUVs { get; set; }
    /// <summary>网格自带的 baseColor 纹理；没有则为空。</summary>
    public Doudou.Core.Imaging.RgbaImage? Texture { get; set; }
    /// <summary>OBJ 的 V 轴通常以左下角为原点，采样时需要翻转；GLB 由混元导出时不需要。</summary>
    public bool TextureFlipV { get; set; }
    public List<int> Indices { get; } = new();          // 每 3 个一组

    public int TriangleCount => Indices.Count / 3;

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in Vertices) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        return (mn, mx);
    }

    public void Transform(Func<Vector3, Vector3> f)
    {
        for (int i = 0; i < Vertices.Count; i++) Vertices[i] = f(Vertices[i]);
    }
}

public static class MeshLoader
{
    public static TriMesh Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".obj" => LoadObj(path),
        ".stl" => LoadStl(path),
        ".glb" => LoadGlb(path),
        _ => throw new NotSupportedException("不支持的网格格式：" + Path.GetExtension(path)),
    };

    public static TriMesh LoadObj(string path)
    {
        var verts = new List<Vector3>();
        var colors = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faces = new List<(int V, int Vt)[]>();
        string? mtl = null;
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                verts.Add(new Vector3(float.Parse(t[1], inv), float.Parse(t[2], inv), float.Parse(t[3], inv)));
                if (t.Length >= 7) colors.Add(new Vector3(float.Parse(t[4], inv), float.Parse(t[5], inv), float.Parse(t[6], inv)));
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal))
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                float u = t.Length > 1 ? float.Parse(t[1], inv) : 0;
                float v = t.Length > 2 ? float.Parse(t[2], inv) : 0;
                uvs.Add(new Vector2(u, v));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var arr = new (int V, int Vt)[t.Length - 1];
                for (int k = 1; k < t.Length; k++)
                {
                    var parts = t[k].Split('/');
                    int v = int.Parse(parts[0], inv);
                    v = v > 0 ? v - 1 : verts.Count + v;
                    int vt = -1;
                    if (parts.Length > 1 && parts[1].Length > 0)
                    {
                        vt = int.Parse(parts[1], inv);
                        vt = vt > 0 ? vt - 1 : uvs.Count + vt;
                    }
                    arr[k - 1] = (v, vt);
                }
                if (arr.Length >= 3) faces.Add(arr);
            }
            else if (line.StartsWith("mtllib ", StringComparison.Ordinal))
                mtl = line["mtllib ".Length..].Trim();
        }

        var m = new TriMesh();
        bool hasUv = uvs.Count > 0 && faces.Any(f => f.Any(c => c.Vt >= 0));
        if (!hasUv)
        {
            m.Vertices.AddRange(verts);
            if (colors.Count == verts.Count && colors.Count > 0) m.VertexColors = colors;
            foreach (var f in faces)
                for (int k = 1; k + 1 < f.Length; k++)
                {
                    m.Indices.Add(f[0].V); m.Indices.Add(f[k].V); m.Indices.Add(f[k + 1].V);
                }
            return m;
        }

        var map = new Dictionary<(int V, int Vt), int>();
        m.VertexUVs = new List<Vector2>();
        foreach (var f in faces)
        {
            var idx = new int[f.Length];
            for (int k = 0; k < f.Length; k++)
            {
                var key = f[k];
                if (!map.TryGetValue(key, out int ni))
                {
                    ni = m.Vertices.Count;
                    map[key] = ni;
                    m.Vertices.Add(verts[Math.Clamp(key.V, 0, verts.Count - 1)]);
                    m.VertexUVs.Add(key.Vt >= 0 && key.Vt < uvs.Count ? uvs[key.Vt] : Vector2.Zero);
                }
                idx[k] = ni;
            }
            for (int k = 1; k + 1 < idx.Length; k++)
            {
                m.Indices.Add(idx[0]); m.Indices.Add(idx[k]); m.Indices.Add(idx[k + 1]);
            }
        }
        m.TextureFlipV = true;
        if (!string.IsNullOrWhiteSpace(mtl))
        {
            try
            {
                var mtlPath = Path.Combine(Path.GetDirectoryName(path) ?? "", mtl);
                if (File.Exists(mtlPath))
                {
                    foreach (var raw in File.ReadLines(mtlPath))
                    {
                        var l = raw.Trim();
                        if (!l.StartsWith("map_Kd", StringComparison.OrdinalIgnoreCase)) continue;
                        var texName = l["map_Kd".Length..].Trim().Trim('"');
                        var texPath = Path.Combine(Path.GetDirectoryName(mtlPath) ?? "", texName);
                        if (File.Exists(texPath)) { m.Texture = Doudou.Core.Imaging.RgbaImage.Load(texPath); break; }
                    }
                }
            }
            catch (Exception) { m.Texture = null; }
        }
        return m;
    }

    public static TriMesh LoadStl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var m = new TriMesh();
        bool ascii = bytes.Length > 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "solid"
                     && Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512)).Contains("facet");
        if (ascii)
        {
            foreach (var raw in File.ReadLines(path))
            {
                var l = raw.Trim();
                if (!l.StartsWith("vertex")) continue;
                var t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                m.Indices.Add(m.Vertices.Count);
                m.Vertices.Add(new Vector3(float.Parse(t[1], CultureInfo.InvariantCulture),
                    float.Parse(t[2], CultureInfo.InvariantCulture), float.Parse(t[3], CultureInfo.InvariantCulture)));
            }
            return m;
        }
        int n = BitConverter.ToInt32(bytes, 80);
        for (int i = 0; i < n; i++)
        {
            int o = 84 + i * 50 + 12;
            for (int k = 0; k < 3; k++)
            {
                m.Indices.Add(m.Vertices.Count);
                m.Vertices.Add(new Vector3(BitConverter.ToSingle(bytes, o + k * 12), BitConverter.ToSingle(bytes, o + k * 12 + 4), BitConverter.ToSingle(bytes, o + k * 12 + 8)));
            }
        }
        return m;
    }

    /// <summary>最小 glTF 二进制读取：所有 mesh primitive 的 POSITION + indices（忽略节点变换以外的材质）。</summary>
    public static TriMesh LoadGlb(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(bytes, 0) != 0x46546C67) throw new InvalidDataException("不是 GLB 文件");
        int jsonLen = BitConverter.ToInt32(bytes, 12);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLen)).RootElement;
        int binStart = 20 + jsonLen + 8;
        var m = new TriMesh();
        var accessors = json.GetProperty("accessors");
        var views = json.GetProperty("bufferViews");

        (int Offset, int Count, int Stride, int CompType, string Type) Acc(int i)
        {
            var a = accessors[i];
            var bv = views[a.GetProperty("bufferView").GetInt32()];
            int off = binStart + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0)
                      + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;
            return (off, a.GetProperty("count").GetInt32(), stride, a.GetProperty("componentType").GetInt32(), a.GetProperty("type").GetString()!);
        }

        // 节点变换：只处理平移/缩放/矩阵的一层，足够应付常见导出
        var meshXf = new Dictionary<int, Matrix4x4>();
        if (json.TryGetProperty("nodes", out var nodes))
            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("mesh", out var mi)) continue;
                var xf = Matrix4x4.Identity;
                if (node.TryGetProperty("matrix", out var mat))
                {
                    var f = mat.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
                    xf = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                }
                else
                {
                    var s = node.TryGetProperty("scale", out var sc) ? new Vector3((float)sc[0].GetDouble(), (float)sc[1].GetDouble(), (float)sc[2].GetDouble()) : Vector3.One;
                    var r = node.TryGetProperty("rotation", out var ro) ? new Quaternion((float)ro[0].GetDouble(), (float)ro[1].GetDouble(), (float)ro[2].GetDouble(), (float)ro[3].GetDouble()) : Quaternion.Identity;
                    var t = node.TryGetProperty("translation", out var tr) ? new Vector3((float)tr[0].GetDouble(), (float)tr[1].GetDouble(), (float)tr[2].GetDouble()) : Vector3.Zero;
                    xf = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
                }
                meshXf[mi.GetInt32()] = xf;
            }

        // 读取第一张 baseColor 纹理（GLB 内嵌 PNG/JPEG）
        Doudou.Core.Imaging.RgbaImage? texture = null;
        try
        {
            if (json.TryGetProperty("materials", out var mats))
                foreach (var mat in mats.EnumerateArray())
                {
                    if (!mat.TryGetProperty("pbrMetallicRoughness", out var pbr)) continue;
                    if (!pbr.TryGetProperty("baseColorTexture", out var bct)) continue;
                    int ti = bct.GetProperty("index").GetInt32();
                    var texNode = json.GetProperty("textures")[ti];
                    int si = texNode.GetProperty("source").GetInt32();
                    var imgNode = json.GetProperty("images")[si];
                    if (!imgNode.TryGetProperty("bufferView", out var bvi)) continue;
                    var bv = views[bvi.GetInt32()];
                    int off = binStart + (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0);
                    int len = bv.GetProperty("byteLength").GetInt32();
                    texture = Doudou.Core.Imaging.RgbaImage.FromEncoded(bytes.AsSpan(off, len).ToArray());
                    break;
                }
        }
        catch (Exception) { texture = null; }

        bool uvOk = true;
        var meshes = json.GetProperty("meshes");
        for (int mi = 0; mi < meshes.GetArrayLength(); mi++)
        {
            var xf = meshXf.TryGetValue(mi, out var x) ? x : Matrix4x4.Identity;
            foreach (var prim in meshes[mi].GetProperty("primitives").EnumerateArray())
            {
                var attrs = prim.GetProperty("attributes");
                var pa = Acc(attrs.GetProperty("POSITION").GetInt32());
                List<Vector2>? primUv = null;
                if (attrs.TryGetProperty("TEXCOORD_0", out var uvNode) && Acc(uvNode.GetInt32()) is var ua && ua.CompType == 5126)
                {
                    int uvStride = ua.Stride == 0 ? 8 : ua.Stride;
                    primUv = new List<Vector2>(ua.Count);
                    for (int i = 0; i < ua.Count; i++)
                    {
                        int o = ua.Offset + i * uvStride;
                        primUv.Add(new Vector2(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4)));
                    }
                }
                int baseV = m.Vertices.Count;
                int stride = pa.Stride == 0 ? 12 : pa.Stride;
                for (int i = 0; i < pa.Count; i++)
                {
                    int o = pa.Offset + i * stride;
                    var v = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
                    m.Vertices.Add(Vector3.Transform(v, xf));
                }
                if (prim.TryGetProperty("indices", out var ii))
                {
                    var ia = Acc(ii.GetInt32());
                    int size = ia.CompType switch { 5121 => 1, 5123 => 2, _ => 4 };
                    for (int i = 0; i < ia.Count; i++)
                    {
                        int o = ia.Offset + i * size;
                        int v = size == 1 ? bytes[o] : size == 2 ? BitConverter.ToUInt16(bytes, o) : (int)BitConverter.ToUInt32(bytes, o);
                        m.Indices.Add(baseV + v);
                    }
                }
                else
                    for (int i = 0; i < pa.Count; i++) m.Indices.Add(baseV + i);
                if (primUv != null && primUv.Count == pa.Count && uvOk)
                {
                    m.VertexUVs ??= new List<Vector2>();
                    m.VertexUVs.AddRange(primUv);
                }
                else uvOk = false;
            }
        }
        if (!uvOk) m.VertexUVs = null;
        m.Texture = texture;
        m.TextureFlipV = false;
        return m;
    }
}
