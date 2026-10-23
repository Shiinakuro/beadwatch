using System.IO.Compression;
using System.Text.Json;
using Doudou.Core.Color;
using Doudou.Core.Model;
using Doudou.Core.Recon;

namespace Doudou.Core.IO;

/// <summary>工程元数据（图纸标题、作者、参考视图等）。</summary>
public sealed class ProjectMeta
{
    public string Title { get; set; } = "未命名作品";
    public string Author { get; set; } = "";
    public string Modeler { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Notes { get; set; } = "禁止二传、二改、商用";
    public string Watermark { get; set; } = "";
    public List<RefView> Views { get; set; } = new();
}

public sealed class RefView
{
    public ViewKind Kind { get; set; }
    /// <summary>工程包内的相对路径（views/front.png）。</summary>
    public string File { get; set; } = "";
}

/// <summary>
/// 工程文件 .doudou（zip）：
///   project.json —— 元数据、调色板、零件表、尺寸
///   voxels.bin   —— Colors(ushort[]) + Parts(ushort[])，小端
///   views/*.png  —— 参考视图
/// </summary>
public static class ProjectFile
{
    private sealed class Dto
    {
        public int Version { get; set; } = 1;
        public int SX { get; set; }
        public int SY { get; set; }
        public int SZ { get; set; }
        public string BrandId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<ColorDto> Palette { get; set; } = new();
        public List<PartInfoDto> Parts { get; set; } = new();
        public ProjectMeta Meta { get; set; } = new();
    }
    private sealed class ColorDto { public string Code { get; set; } = ""; public string Hex { get; set; } = ""; public bool Any { get; set; } public string Note { get; set; } = ""; }
    private sealed class PartInfoDto
    {
        public int Id { get; set; } public string Name { get; set; } = ""; public int Axis { get; set; } = -1;
        public bool Visible { get; set; } = true; public bool Locked { get; set; } public bool Flip { get; set; }
        public string Group { get; set; } = ""; public string Note { get; set; } = ""; public string Tint { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string path, VoxelModel m, ProjectMeta meta, IReadOnlyDictionary<ViewKind, string>? viewFiles = null)
    {
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var newMeta = JsonSerializer.Deserialize<ProjectMeta>(JsonSerializer.Serialize(meta))!;
            if (viewFiles != null)
            {
                newMeta.Views.Clear();
                foreach (var (kind, file) in viewFiles)
                {
                    if (!File.Exists(file)) continue;
                    var name = $"views/{kind.ToString().ToLowerInvariant()}{Path.GetExtension(file).ToLowerInvariant()}";
                    zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                    newMeta.Views.Add(new RefView { Kind = kind, File = name });
                }
            }
            var dto = new Dto
            {
                SX = m.SX, SY = m.SY, SZ = m.SZ, BrandId = m.BrandId, Name = m.Name, Meta = newMeta,
                Palette = m.Palette.Skip(1).Select(c => new ColorDto { Code = c.Code, Hex = c.Rgb.Hex, Any = c.IsAny, Note = c.Note }).ToList(),
                Parts = m.PartList.Select(p => new PartInfoDto
                {
                    Id = p.Id, Name = p.Name, Axis = p.PlaneAxis, Visible = p.Visible, Locked = p.Locked, Flip = p.Flip,
                    Group = p.Group, Note = p.Note, Tint = p.Tint.Hex,
                }).ToList(),
            };
            using (var s = zip.CreateEntry("project.json", CompressionLevel.Optimal).Open())
                JsonSerializer.Serialize(s, dto, Json);
            using (var s = zip.CreateEntry("voxels.bin", CompressionLevel.Optimal).Open())
            using (var w = new BinaryWriter(s))
            {
                foreach (var c in m.Colors) w.Write(c);
                foreach (var p in m.Parts) w.Write(p);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>读取工程。参考视图解压到 extractDir（若给出）并返回路径表。</summary>
    public static (VoxelModel Model, ProjectMeta Meta, Dictionary<ViewKind, string> Views) Load(string path, string? extractDir = null)
    {
        using var zip = ZipFile.OpenRead(path);
        Dto dto;
        using (var s = zip.GetEntry("project.json")!.Open()) dto = JsonSerializer.Deserialize<Dto>(s)!;
        var m = new VoxelModel(dto.SX, dto.SY, dto.SZ) { BrandId = dto.BrandId, Name = dto.Name };
        foreach (var c in dto.Palette) m.Palette.Add(new BeadColor { Code = c.Code, Rgb = Rgb.FromHex(c.Hex), IsAny = c.Any, Note = c.Note });
        foreach (var p in dto.Parts)
            m.PartList.Add(new PartInfo
            {
                Id = p.Id, Name = p.Name, PlaneAxis = p.Axis, Visible = p.Visible, Locked = p.Locked, Flip = p.Flip, Group = p.Group, Note = p.Note,
                Tint = string.IsNullOrEmpty(p.Tint) ? VoxelModel.PartTint(p.Id) : Rgb.FromHex(p.Tint),
            });
        using (var s = zip.GetEntry("voxels.bin")!.Open())
        using (var r = new BinaryReader(s))
        {
            for (int i = 0; i < m.Count; i++) m.Colors[i] = r.ReadUInt16();
            for (int i = 0; i < m.Count; i++) m.Parts[i] = r.ReadUInt16();
        }
        var views = new Dictionary<ViewKind, string>();
        if (extractDir != null)
        {
            Directory.CreateDirectory(extractDir);
            foreach (var v in dto.Meta.Views)
            {
                var e = zip.GetEntry(v.File);
                if (e == null) continue;
                var outp = Path.Combine(extractDir, Path.GetFileName(v.File));
                e.ExtractToFile(outp, overwrite: true);
                views[v.Kind] = outp;
            }
        }
        m.Touch();
        return (m, dto.Meta, views);
    }
}
