using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Imaging;
using Doudou.Core.IO;
using Doudou.Core.Model;

namespace Doudou.Core.Recon;

public sealed class MeshImportOptions
{
    public int TargetHeight { get; set; } = 64;
    public string BrandId { get; set; } = "mard221";
    public int MaxColors { get; set; } = 24;
    public string Name { get; set; } = "导入模型";
    /// <summary>可选参考视图：无纹理时用于投影上色。</summary>
    public IReadOnlyList<ViewImage> Views { get; set; } = Array.Empty<ViewImage>();
    /// <summary>即使模型带纹理也优先用参考视图取色（AI 生成的纹理常带烘焙光照）。</summary>
    public bool PreferViews { get; set; }
    /// <summary>去除模型纹理/参考图里的烘焙光照，只保留相对稳定的底色。</summary>
    public bool RemoveLighting { get; set; } = true;
    /// <summary>有参考图时，用参考图的低频颜色校正纹理的偏色（保留纹理细节）。</summary>
    public bool CorrectTextureColor { get; set; } = true;
    /// <summary>把带光照的 AI 纹理压成少数底色（会丢失一点纹理细节，但不再灰/绿/蓝一片）。</summary>
    public bool FlattenTextureColors { get; set; } = true;
}

public sealed class MeshImportResult
{
    public VoxelModel Model { get; init; } = null!;
    public bool UsedTexture { get; init; }
    public bool UsedViews { get; init; }
    public bool RotatedZUp { get; init; }
}

/// <summary>
/// 导入 OBJ/GLB/STL：自动把 Z-up 转成 Y-up，按目标豆高体素化；
/// 带 UV+纹理的 GLB/OBJ 直接用纹理上色，否则用参考视图投影，最后量化到色卡。
/// </summary>
public static class MeshImporter
{
    public static MeshImportResult Import(TriMesh mesh, PaletteLibrary lib, MeshImportOptions opt,
        Action<string>? say = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool rotated = OrientMesh(mesh, opt.Views, opt.TargetHeight, say);
        say?.Invoke($"网格 {mesh.TriangleCount:N0} 个三角面，体素化到 {opt.TargetHeight} 颗高…");
        var occ = Voxelizer.Voxelize(mesh, opt.TargetHeight);
        say?.Invoke($"体素：{occ.SX}×{occ.SY}×{occ.SZ}，{occ.Count():N0} 颗");

        var texColors = SampleTexture(mesh);
        if (opt.Views.Count > 0 && (opt.PreferViews || opt.CorrectTextureColor))
        {
            // 参考图通常比 AI 纹理更接近真实底色；先去光影再投影/校正。
            foreach (var v in opt.Views)
                if (!v.IsAi) v.FlattenWith(null);
        }
        VoxelModel model;
        bool usedTexture = false, usedViews = false;
        if (texColors != null && !(opt.PreferViews && opt.Views.Count > 0))
        {
            say?.Invoke("使用模型自带纹理上色…");
            var colors = ColorFromVertices(mesh, occ, texColors, say, ct);
            if (opt.FlattenTextureColors)
            {
                // 颜色只看与模型严格对齐的纹理，不依赖可能对不齐的参考图投影。
                colors = RecolorTexture(occ, colors, Array.Empty<Lab?>(), say);
            }
            if (opt.RemoveLighting && !opt.FlattenTextureColors) colors = SurfaceDeLight(occ, colors, say);
            model = Quantizer.Build(occ, colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedTexture = true;
        }
        else if (opt.Views.Count > 0)
        {
            say?.Invoke("模型无纹理，使用参考视图投影上色…");
            var aligned = ReconstructPipeline.BestOrientation(occ, opt.Views, say);
            var proj = ColorProjector.Project(aligned, opt.Views);
            var colors = opt.RemoveLighting ? SurfaceDeLight(aligned, proj.Colors, say) : proj.Colors;
            model = Quantizer.Build(aligned, colors, lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
            usedViews = true;
        }
        else
        {
            say?.Invoke("模型无纹理，也没有参考视图：生成单色体素。");
            model = Quantizer.Build(occ, new Lab?[occ.Cells.Length], lib, new QuantizeOptions { BrandId = opt.BrandId, MaxColors = opt.MaxColors }, opt.Name);
        }

        int cleaned = SurfaceColorCleaner.Clean(model);
        if (cleaned > 0) model.CompactPalette();
        say?.Invoke($"上色完成：{model.Palette.Count - 1} 色，清理 {cleaned} 颗。");
        return new MeshImportResult { Model = model, UsedTexture = usedTexture, UsedViews = usedViews, RotatedZUp = rotated };
    }

    /// <summary>导入前检查朝向：有参考视图时用 6 个轴向 + 轮廓吻合度选最正的方向；没有视图时按最长轴自动转正。</summary>
    private static bool OrientMesh(TriMesh mesh, IReadOnlyList<ViewImage> views, int targetHeight, Action<string>? say)
    {
        var original = mesh.Vertices.ToArray();
        if (views.Count > 0)
        {
            var candidates = new (string Name, Func<Vector3, Vector3> F)[]
            {
                ("Y-up（无需旋转）", v => v),
                ("X+90°", v => new Vector3(v.X, -v.Z, v.Y)),
                ("X-90°", v => new Vector3(v.X, v.Z, -v.Y)),
                ("X 180°", v => new Vector3(v.X, -v.Y, -v.Z)),
                ("Z+90°", v => new Vector3(v.Y, -v.X, v.Z)),
                ("Z-90°", v => new Vector3(-v.Y, v.X, v.Z)),
            };
            double best = -1;
            int bestIndex = 0;
            for (int ci = 0; ci < candidates.Length; ci++)
            {
                for (int i = 0; i < original.Length; i++) mesh.Vertices[i] = candidates[ci].F(original[i]);
                var (mn, mx) = mesh.Bounds();
                if (mx.Y - mn.Y <= 1e-6f) continue;
                var occ = Voxelizer.Voxelize(mesh, Math.Min(targetHeight, 48));
                var aligned = ReconstructPipeline.BestOrientation(occ, views, null);
                double score = 0;
                foreach (var v in views)
                    score += SilhouetteFit.IoU(aligned, v) * (v.Kind == ViewKind.Front ? 1.5 : 1);
                if (score > best) { best = score; bestIndex = ci; }
            }
            for (int i = 0; i < original.Length; i++) mesh.Vertices[i] = candidates[bestIndex].F(original[i]);
            double avg = best / views.Sum(v => v.Kind == ViewKind.Front ? 1.5 : 1);
            say?.Invoke($"朝向检查：{candidates[bestIndex].Name}，轮廓吻合度 {avg:P0}。");
            if (avg < 0.5) say?.Invoke("朝向置信度偏低：导入后可在编辑器里旋转校正。");
            return bestIndex != 0;
        }

        var (bMin, bMax) = mesh.Bounds();
        float ex = bMax.X - bMin.X, ey = bMax.Y - bMin.Y, ez = bMax.Z - bMin.Z;
        if (ez > ey * 1.15f && ez >= ex)
        {
            mesh.Transform(v => new Vector3(v.X, v.Z, -v.Y));
            say?.Invoke("检测到 Z-up / 躺倒模型，已自动转正为 Y-up。");
            return true;
        }
        if (ex > ey * 1.15f && ex >= ez)
        {
            mesh.Transform(v => new Vector3(-v.Y, v.X, v.Z));
            say?.Invoke("检测到 X-up 模型，已自动转正为 Y-up。");
            return true;
        }
        return false;
    }

    /// <summary>按顶点 UV 从 baseColor 纹理取色。UV 以左下角为原点，图片以左上角为原点。</summary>
    private static Rgb[]? SampleTexture(TriMesh mesh)
    {
        var tex = mesh.Texture;
        var uvs = mesh.VertexUVs;
        if (tex == null || uvs == null || uvs.Count != mesh.Vertices.Count) return null;
        var colors = new Rgb[uvs.Count];
        for (int i = 0; i < uvs.Count; i++)
        {
            float u = uvs[i].X - MathF.Floor(uvs[i].X);
            float v = uvs[i].Y - MathF.Floor(uvs[i].Y);
            int x = Math.Clamp((int)(u * (tex.W - 1) + 0.5f), 0, tex.W - 1);
            int y = Math.Clamp((int)((mesh.TextureFlipV ? 1f - v : v) * (tex.H - 1) + 0.5f), 0, tex.H - 1);
            colors[i] = tex.GetRgb(x, y);
        }
        return colors;
    }

    /// <summary>把顶点颜色按最近顶点分配给体素。KD-tree 保证每个体素都有命中，避免大三角形/平坦面漏色。</summary>
    private static Lab?[] ColorFromVertices(TriMesh mesh, Occupancy occ, Rgb[] vertexColors, Action<string>? say, CancellationToken ct)
    {
        var (mn, mx) = mesh.Bounds();
        float scale = occ.SY / (mx.Y - mn.Y);
        int nv = mesh.Vertices.Count;
        var pts = new Vector3[nv];
        for (int i = 0; i < nv; i++) pts[i] = (mesh.Vertices[i] - mn) * scale;
        var tree = new KdTree(pts);

        var colors = new Lab?[occ.Cells.Length];
        int found = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    if (!occ[x, y, z]) continue;
                    int vi = tree.Nearest(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
                    var rgb = vertexColors[vi];
                    colors[occ.Index(x, y, z)] = ColorMath.ToLab(rgb.R, rgb.G, rgb.B);
                    found++;
                    if (found % 25000 == 0) ct.ThrowIfCancellationRequested();
                }
        say?.Invoke($"纹理取色：{found:N0} 颗全部命中。");
        return colors;
    }

    /// <summary>把参考图压成少数底色类别图：脸部灰白高光归皮肤，头发高光归深色，保留金边和眼睛。</summary>
    private static IReadOnlyList<ViewImage> BuildSemanticViews(IReadOnlyList<ViewImage> views)
    {
        var list = new List<ViewImage>(views.Count);
        foreach (var v in views)
        {
            var src = (v.ColorSource ?? v).Image;
            var sil = v.Sil;
            var raw = new SurfaceClass?[src.W * src.H];
            for (int y = 0; y < src.H; y++)
                for (int x = 0; x < src.W; x++)
                {
                    if (!sil.At(x, y)) continue;
                    var c = src.GetRgb(x, y);
                    raw[y * src.W + x] = RawClass(ColorMath.ToLab(c));
                }
            // 白色/灰色高光按周围大窗口的类别重新归类：脸/手归皮肤，头发归深色，衣服归蓝色；
            // 靴子/围裙的白仍由周围白色占多数，不会被改掉。
            int w = src.W, h = src.H;
            var clsMap = new int[w * h];
            Array.Fill(clsMap, -1);
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] is { } rc) clsMap[i] = (int)rc;
            var pref = new int[7][];
            for (int c = 0; c < 7; c++) pref[c] = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, cl = clsMap[i];
                    for (int c = 0; c < 7; c++)
                        pref[c][(y + 1) * (w + 1) + x + 1] = pref[c][y * (w + 1) + x + 1]
                            + pref[c][(y + 1) * (w + 1) + x] - pref[c][y * (w + 1) + x] + (cl == c ? 1 : 0);
                }
            int Sum(int c, int x0, int y0, int x1, int y1) =>
                pref[c][y1 * (w + 1) + x1] - pref[c][y0 * (w + 1) + x1] - pref[c][y1 * (w + 1) + x0] + pref[c][y0 * (w + 1) + x0];
            const int Win = 8;
            var cleaned = (int[])clsMap.Clone();
            var cc = new int[7];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, cl = clsMap[i];
                    if (cl < 0) continue;
                    int x0 = Math.Max(0, x - Win), y0 = Math.Max(0, y - Win), x1 = Math.Min(w, x + Win + 1), y1 = Math.Min(h, y + Win + 1);
                    int total = 0;
                    Array.Clear(cc);
                    for (int c = 0; c < 7; c++) { cc[c] = Sum(c, x0, y0, x1, y1); total += cc[c]; }
                    if (total <= 0) continue;
                    if (cl == (int)SurfaceClass.White)
                    {
                        double wd = (double)cc[(int)SurfaceClass.White] / total;
                        double skin = (double)cc[(int)SurfaceClass.Skin] / total;
                        if (wd < 0.45 && skin >= 0.20 && cc[(int)SurfaceClass.Skin] >= cc[(int)SurfaceClass.Dark])
                            cleaned[i] = (int)SurfaceClass.Skin;
                        else if (wd < 0.45 && cc[(int)SurfaceClass.Dark] > total * 0.40)
                            cleaned[i] = (int)SurfaceClass.Dark;
                        else if (wd < 0.45 && cc[(int)SurfaceClass.Blue] > total * 0.40)
                            cleaned[i] = (int)SurfaceClass.Blue;
                    }
                    else if (cl == (int)SurfaceClass.Blue)
                    {
                        var (_, _, vv) = Hsv(ColorMath.ToLab(src.GetRgb(x, y)));
                        double bd = (double)cc[(int)SurfaceClass.Blue] / total;
                        if (bd < 0.35 && vv < 0.62)
                        {
                            int bestC = -1, bestN = 0;
                            for (int c = 0; c < 7; c++)
                                if (c != (int)SurfaceClass.Blue && cc[c] > bestN) { bestN = cc[c]; bestC = c; }
                            if (bestC >= 0 && bestN > total * 0.30) cleaned[i] = bestC;
                        }
                    }
                }
            clsMap = cleaned;

            var outp = new RgbaImage(src.W, src.H);
            var baseRgb = ClassColors.Select(ColorMath.FromLab).ToArray();
            var counts = new int[7];
            for (int y = 0; y < src.H; y++)
                for (int x = 0; x < src.W; x++)
                {
                    int i = y * src.W + x;
                    if (clsMap[i] < 0) continue;
                    Array.Clear(counts);
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int ax = x + dx, ay = y + dy;
                            if (ax < 0 || ay < 0 || ax >= src.W || ay >= src.H) continue;
                            int c = clsMap[ay * src.W + ax];
                            if (c >= 0) counts[c]++;
                        }
                    int best = clsMap[i];
                    for (int k = 0; k < 7; k++) if (counts[k] > counts[best]) best = k;
                    var rgb = baseRgb[best];
                    int o = i * 4;
                    outp.Px[o] = rgb.R; outp.Px[o + 1] = rgb.G; outp.Px[o + 2] = rgb.B; outp.Px[o + 3] = 255;
                }
            var semantic = new ViewImage { Kind = v.Kind, Image = outp, Sil = sil, SourcePath = v.SourcePath };
            list.Add(new ViewImage { Kind = v.Kind, Image = v.Image, Sil = sil, SourcePath = v.SourcePath, ColorSource = semantic, IsAi = true });
        }
        return list;
    }

    private enum SurfaceClass { Dark, Blue, Cyan, Gold, Red, Skin, White }

    private static readonly Lab[] ClassColors =
    {
        new(9, 0, -3),      // 深色头发
        new(39, 6, -39),    // 蓝色衣服
        new(68, -16, -28),  // 眼睛的青蓝
        new(66, 5, 45),     // 金色饰边
        new(45, 38, 24),    // 红色花纹/嘴唇
        new(81, 8, 13),     // 皮肤
        new(94, -1, 4),     // 白色围裙/靴子/眼白
    };

    private static (double H, double S, double V) Hsv(Lab c)
    {
        var rgb = ColorMath.FromLab(c);
        double r = rgb.R / 255.0, g = rgb.G / 255.0, b = rgb.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 1e-6 ? 0 : d / max, max);
    }

    private static SurfaceClass RawClass(Lab c)
    {
        var (h, s, v) = Hsv(c);
        if (s < 0.13 && v > 0.55) return SurfaceClass.White;
        if (v < 0.30) return SurfaceClass.Dark;
        if (h >= 168 && h <= 262 && s >= 0.22) return v > 0.78 && h < 225 ? SurfaceClass.Cyan : SurfaceClass.Blue;
        if (h >= 24 && h <= 78 && s >= 0.30) return SurfaceClass.Gold;
        if ((h < 18 || h > 340) && s >= 0.30) return SurfaceClass.Red;
        if (h >= 4 && h <= 52 && s >= 0.08 && v >= 0.30) return SurfaceClass.Skin;
        return SurfaceClass.White;
    }

    /// <summary>
    /// 只清理纹理自身的白色/灰色光影：靠近皮肤的白色扩成皮肤，靠近深色头发的归深色，
    /// 靠近蓝衣服的归蓝色；白色围裙/靴子仍由周围白色占多数而保留。眼睛附近的白色不清理。
    /// </summary>
    private static SurfaceClass?[] CleanTextureClasses(Occupancy occ, bool[] surface, SurfaceClass?[] raw, Lab?[] texture, bool[] detail)
    {
        int n = raw.Length;
        var cls = (SurfaceClass?[])raw.Clone();
        var counts = new int[7];
        for (int pass = 0; pass < 4; pass++)
        {
            var next = (SurfaceClass?[])cls.Clone();
            for (int i = 0; i < n; i++)
            {
                if (!surface[i] || detail[i]) continue;
                var cur = cls[i];
                if (cur is not (SurfaceClass.White or SurfaceClass.Blue or SurfaceClass.Cyan)) continue;
                if (texture[i] is { } tc && cur is SurfaceClass.Blue or SurfaceClass.Cyan)
                {
                    var (_, ss, vv) = Hsv(tc);
                    if (ss >= 0.35 || vv < 0.30 || vv > 0.78) continue;
                }
                int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
                Array.Clear(counts);
                int total = 0;
                for (int dz = -5; dz <= 5; dz++)
                    for (int dy = -5; dy <= 5; dy++)
                        for (int dx = -5; dx <= 5; dx++)
                        {
                            if (dx * dx + dy * dy + dz * dz > 25) continue;
                            int ax = x + dx, ay = y + dy, az = z + dz;
                            if (!occ.In(ax, ay, az)) continue;
                            int j = occ.Index(ax, ay, az);
                            if (!surface[j] || cls[j] is not { } c) continue;
                            counts[(int)c]++;
                            total++;
                        }
                if (total < 8) continue;
                int blueRed = counts[(int)SurfaceClass.Blue] + counts[(int)SurfaceClass.Cyan] + counts[(int)SurfaceClass.Red];
                if (cur == SurfaceClass.White && blueRed >= 2) continue; // 眼白/花纹附近保留
                int sk = counts[(int)SurfaceClass.Skin];
                int wh = counts[(int)SurfaceClass.White];
                int dk = counts[(int)SurfaceClass.Dark];
                if (sk >= 2 && sk >= dk && (cur != SurfaceClass.White || wh < total * 0.60))
                    next[i] = SurfaceClass.Skin;
                else if (cur == SurfaceClass.White && dk >= total * 0.50) next[i] = SurfaceClass.Dark;
                else if (cur == SurfaceClass.White && counts[(int)SurfaceClass.Blue] >= total * 0.50) next[i] = SurfaceClass.Blue;
                else if (cur == SurfaceClass.White && counts[(int)SurfaceClass.Gold] >= total * 0.35) next[i] = SurfaceClass.Gold;
                else if (cur == SurfaceClass.White && y > occ.SY * 0.60 && sk >= 1 && blueRed < 2) next[i] = SurfaceClass.Skin;
            }
            cls = next;
        }
        return cls;
    }

    /// <summary>
    /// 纹理空间是跟模型严格对齐的，参考图空间不一定对齐。
    /// 所以按纹理颜色分类到少数底色，再用参考图的平滑类别做歧义消解：
    /// 白/灰优先归到皮肤或深色；橄榄色高光归回深色；深蓝高光归回蓝色。
    /// </summary>
    private static Lab?[] RecolorTexture(Occupancy occ, Lab?[] texture, Lab?[] reference, Action<string>? say)
    {
        int n = texture.Length;
        var surface = new bool[n];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    foreach (var d in Dir6.All)
                        if (!occ[x + d.X, y + d.Y, z + d.Z]) { surface[i] = true; break; }
                }

        var raw = new SurfaceClass?[n];
        var src = new int[n];
        Array.Fill(src, -1);
        for (int i = 0; i < n; i++)
        {
            if (!surface[i]) continue;
            if (texture[i] is { } t) raw[i] = RawClass(t);
            if (i < reference.Length && reference[i] is { } r) src[i] = (int)RawClass(r);
        }
        var detailMask = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!surface[i] || raw[i] is not { } rc) continue;
            if (rc is not (SurfaceClass.Blue or SurfaceClass.Cyan or SurfaceClass.Red or SurfaceClass.Gold)) continue;
            if (texture[i] is not { } tc) continue;
            var (_, ds, dv) = Hsv(tc);
            if (ds < 0.35 || dv < 0.2) continue;
            int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
            int same = 0;
            for (int dz = -2; dz <= 2; dz++)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int ax = x + dx, ay = y + dy, az = z + dz;
                        if (!occ.In(ax, ay, az)) continue;
                        int j = occ.Index(ax, ay, az);
                        if (surface[j] && raw[j] == rc) same++;
                    }
            if (same <= 12) detailMask[i] = true;
        }
        var clsMap = CleanTextureClasses(occ, surface, raw, texture, detailMask);

        // 参考图的类别场做空间多数平滑，降低投影错位带来的噪点。
        var srcSmooth = (int[])src.Clone();
        var srcConf = new double[n];
        var classCounts = new int[7];
        for (int pass = 0; pass < 3; pass++)
        {
            var next = (int[])srcSmooth.Clone();
            for (int i = 0; i < n; i++)
            {
                if (!surface[i] || srcSmooth[i] < 0) continue;
                int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
                Array.Clear(classCounts);
                int total = 0;
                for (int dz = -5; dz <= 5; dz++)
                    for (int dy = -5; dy <= 5; dy++)
                        for (int dx = -5; dx <= 5; dx++)
                        {
                            if (dx * dx + dy * dy + dz * dz > 25) continue;
                            int ax = x + dx, ay = y + dy, az = z + dz;
                            if (!occ.In(ax, ay, az)) continue;
                            int j = occ.Index(ax, ay, az);
                            if (!surface[j] || srcSmooth[j] < 0) continue;
                            classCounts[srcSmooth[j]]++;
                            total++;
                        }
                if (total < 6) continue;
                int best = 0;
                for (int k = 1; k < 7; k++) if (classCounts[k] > classCounts[best]) best = k;
                double conf = (double)classCounts[best] / total;
                if (conf >= 0.35) { next[i] = best; srcConf[i] = conf; }
            }
            srcSmooth = next;
        }

        var result = (Lab?[])texture.Clone();
        int changed = 0;
        int hasSrcCount = 0;
        var rawCounts = new int[7];
        var srcCounts = new int[7];
        for (int i = 0; i < n; i++)
        {
            if (!surface[i] || clsMap[i] is not { } c) continue;
            rawCounts[(int)c]++;
            if (srcSmooth[i] >= 0) { hasSrcCount++; srcCounts[srcSmooth[i]]++; }
            var cls = c;
            int srcClass = srcSmooth[i];
            bool hasSrc = srcClass >= 0;
            var (h, s, v) = Hsv(texture[i]!.Value);
            // 先看眼睛/五官附近的高饱和细节，避免把眼白、瞳孔、花纹改掉。
            int detail = 0;
            {
                int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int ax = x + dx, ay = y + dy, az = z + dz;
                            if (!occ.In(ax, ay, az)) continue;
                            int j = occ.Index(ax, ay, az);
                            if (!surface[j] || clsMap[j] is not { } rc) continue;
                            if (rc is SurfaceClass.Dark or SurfaceClass.Blue or SurfaceClass.Cyan or SurfaceClass.Red) detail++;
                        }
            }
            bool nearDetail = detail >= 3;
            if (cls == SurfaceClass.White && nearDetail)
            {
                // 眼白、瞳孔高光保留白色
            }
            else if (!detailMask[i] && hasSrc && srcConf[i] >= 0.40)
            {
                var sc = (SurfaceClass)srcClass;
                switch (sc)
                {
                    case SurfaceClass.Skin:
                        if (cls is SurfaceClass.White or SurfaceClass.Skin)
                            cls = SurfaceClass.Skin;
                        else if (cls is SurfaceClass.Blue or SurfaceClass.Cyan && !nearDetail && v < 0.70)
                            cls = SurfaceClass.Skin;
                        break;
                    case SurfaceClass.Dark:
                        if (cls is SurfaceClass.Gold or SurfaceClass.Dark or SurfaceClass.White)
                            cls = SurfaceClass.Dark;
                        else if (cls is SurfaceClass.Blue or SurfaceClass.Cyan && v < 0.55)
                            cls = SurfaceClass.Dark;
                        break;
                    case SurfaceClass.Blue:
                        if (cls is SurfaceClass.Blue or SurfaceClass.Cyan or SurfaceClass.White)
                            cls = SurfaceClass.Blue;
                        else if (cls is SurfaceClass.Dark && v > 0.30)
                            cls = SurfaceClass.Blue;
                        break;
                    case SurfaceClass.Gold:
                        if (cls is SurfaceClass.Gold or SurfaceClass.White)
                            cls = SurfaceClass.Gold;
                        break;
                    case SurfaceClass.Red:
                        if (cls is SurfaceClass.Red or SurfaceClass.White)
                            cls = SurfaceClass.Red;
                        break;
                    case SurfaceClass.White:
                        if (cls is SurfaceClass.White)
                            cls = SurfaceClass.White;
                        break;
                }
            }
            // 所有表面都落到少数底色，彻底去掉纹理里的明暗渐变。
            result[i] = ClassColors[(int)cls];
            changed++;
        }
        say?.Invoke($"去除光照：统一 {changed:N0} 颗表面豆的底色。");
        return result;
    }

    /// <summary>
    /// 用参考图的低频色场校正纹理色场：纹理保留空间细节，参考图负责把整体偏灰/偏绿/偏蓝的色相拉回。
    /// 只修正表面，内部不参与；色相修正强于亮度修正。
    /// </summary>
    private static Lab?[] ColorTransfer(Occupancy occ, Lab?[] texture, Lab?[] reference, Action<string>? say)
    {
        int n = texture.Length;
        var surface = new bool[n];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    foreach (var d in Dir6.All)
                        if (!occ[x + d.X, y + d.Y, z + d.Z]) { surface[i] = true; break; }
                }

        double[] tl = new double[n], ta = new double[n], tb = new double[n], tw = new double[n];
        double[] rl = new double[n], ra = new double[n], rb = new double[n], rw = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!surface[i]) continue;
            if (texture[i] is { } t) { tl[i] = t.L; ta[i] = t.A; tb[i] = t.B; tw[i] = 1; }
            if (reference[i] is { } r) { rl[i] = r.L; ra[i] = r.A; rb[i] = r.B; rw[i] = 1; }
        }
        // 两轮 5×5×5 均值，得到足够平滑的低频色场；只统计表面豆。
        for (int pass = 0; pass < 2; pass++)
        {
            var ntl = (double[])tl.Clone(); var nta = (double[])ta.Clone(); var ntb = (double[])tb.Clone(); var ntw = (double[])tw.Clone();
            var nrl = (double[])rl.Clone(); var nra = (double[])ra.Clone(); var nrb = (double[])rb.Clone(); var nrw = (double[])rw.Clone();
            for (int i = 0; i < n; i++)
            {
                if (!surface[i]) continue;
                int x = i % occ.SX, t = i / occ.SX, y = t % occ.SY, z = t / occ.SY;
                double sl = 0, sa = 0, sb = 0, sw = 0, ql = 0, qa = 0, qb = 0, qw = 0;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int ax = x + dx, ay = y + dy, az = z + dz;
                            if (!occ.In(ax, ay, az)) continue;
                            int j = occ.Index(ax, ay, az);
                            if (!surface[j]) continue;
                            if (tw[j] > 0) { sl += tl[j]; sa += ta[j]; sb += tb[j]; sw++; }
                            if (rw[j] > 0) { ql += rl[j]; qa += ra[j]; qb += rb[j]; qw++; }
                        }
                if (sw > 0) { ntl[i] = sl / sw; nta[i] = sa / sw; ntb[i] = sb / sw; ntw[i] = 1; }
                if (qw > 0) { nrl[i] = ql / qw; nra[i] = qa / qw; nrb[i] = qb / qw; nrw[i] = 1; }
            }
            tl = ntl; ta = nta; tb = ntb; tw = ntw; rl = nrl; ra = nra; rb = nrb; rw = nrw;
        }

        var result = (Lab?[])texture.Clone();
        int changed = 0;
        for (int i = 0; i < n; i++)
        {
            if (!surface[i] || texture[i] is not { } t || tw[i] <= 0 || rw[i] <= 0) continue;
            double l = Math.Clamp(t.L + (rl[i] - tl[i]) * 0.35, 0, 100);
            double a = Math.Clamp(t.A + (ra[i] - ta[i]) * 0.85, -128, 127);
            double b = Math.Clamp(t.B + (rb[i] - tb[i]) * 0.85, -128, 127);
            if (Math.Abs(l - t.L) + Math.Abs(a - t.A) + Math.Abs(b - t.B) < 1) continue;
            result[i] = new Lab(l, a, b);
            changed++;
        }
        if (changed > 0) say?.Invoke($"参考图校色：调整 {changed:N0} 颗表面豆。");
        return result;
    }

    /// <summary>
    /// 在体素表面做局部多数色平滑：把高光/阴影这类亮度离群点拉回周围的底色，
    /// 同时保留眼睛、描边、金色饰边等高饱和或深色的小细节。
    /// </summary>
    private static Lab?[] SurfaceDeLight(Occupancy occ, Lab?[] colors, Action<string>? say)
    {
        int n = colors.Length;
        var surface = new bool[n];
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!occ.Cells[i]) continue;
                    for (int d = 0; d < 6; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        int nz = z + (d == 4 ? 1 : d == 5 ? -1 : 0);
                        if (!occ.In(nx, ny, nz) || !occ.Cells[occ.Index(nx, ny, nz)]) { surface[i] = true; break; }
                    }
                }

        const int R = 5;
        var result = (Lab?[])colors.Clone();
        var buckets = new Dictionary<(int, int, int), (int Count, double L, double A, double B)>();
        int changed = 0;
        for (int z = 0; z < occ.SZ; z++)
            for (int y = 0; y < occ.SY; y++)
                for (int x = 0; x < occ.SX; x++)
                {
                    int i = occ.Index(x, y, z);
                    if (!surface[i] || colors[i] is not { } center) continue;
                    buckets.Clear();
                    int total = 0, similar = 0;
                    double cl = center.L, ca = center.A, cb = center.B;
                    for (int dz = -R; dz <= R; dz++)
                        for (int dy = -R; dy <= R; dy++)
                            for (int dx = -R; dx <= R; dx++)
                            {
                                if (dx * dx + dy * dy + dz * dz > R * R || (dx | dy | dz) == 0) continue;
                                int ax = x + dx, ay = y + dy, az = z + dz;
                                if (!occ.In(ax, ay, az)) continue;
                                int j = occ.Index(ax, ay, az);
                                if (!surface[j] || colors[j] is not { } c) continue;
                                total++;
                                double dl = c.L - cl, da = c.A - ca, db = c.B - cb;
                                if (dl * dl + da * da + db * db < 400) similar++;
                                var key = ((int)Math.Round(c.L / 10), (int)Math.Round(c.A / 12), (int)Math.Round(c.B / 12));
                                buckets.TryGetValue(key, out var b);
                                buckets[key] = (b.Count + 1, b.L + c.L, b.A + c.A, b.B + c.B);
                            }
                    if (total < 8 || buckets.Count == 0) continue;
                    var dominant = buckets.OrderByDescending(kv => kv.Value.Count).First().Value;
                    if (dominant.Count < total * 0.25) continue;
                    double domL = dominant.L / dominant.Count, domA = dominant.A / dominant.Count, domB = dominant.B / dominant.Count;
                    double chroma = Math.Sqrt(center.A * center.A + center.B * center.B);
                    if (chroma > 45 && similar >= 4) continue; // gold trim, eyes, saturated details
                    bool highlight = center.L - domL > 5;
                    bool shadow = domL - center.L > 8 && center.L > 35;
                    if (!highlight && !shadow) continue;
                    result[i] = new Lab(domL, domA, domB);
                    changed++;
                }
        if (changed > 0) say?.Invoke($"去光照：修正 {changed:N0} 颗表面豆的明暗。");
        return result;
    }

    /// <summary>最小 3D KD-tree：对 87 万顶点做最近邻查询，避免体素漏色。</summary>
    private sealed class KdTree
    {
        private struct Node
        {
            public int Lo, Hi;     // leaf range [Lo,Hi]
            public int Pivot;      // internal split index
            public int Axis;       // -1 = leaf
            public int Left, Right;
        }

        private readonly Vector3[] _p;
        private readonly int[] _ids;
        private readonly List<Node> _nodes = new();

        public KdTree(Vector3[] points)
        {
            _p = points;
            _ids = Enumerable.Range(0, points.Length).ToArray();
            Build(0, _ids.Length, 0);
        }

        private int Build(int lo, int hi, int depth)
        {
            int node = _nodes.Count;
            _nodes.Add(default);
            if (hi - lo <= 8)
            {
                _nodes[node] = new Node { Lo = lo, Hi = hi, Axis = -1 };
                return node;
            }
            int axis = depth % 3;
            Array.Sort(_ids, lo, hi - lo, Comparer<int>.Create((a, b) => _p[a][axis].CompareTo(_p[b][axis])));
            int mid = (lo + hi) / 2;
            int left = Build(lo, mid, depth + 1);
            int right = Build(mid + 1, hi, depth + 1);
            _nodes[node] = new Node { Lo = lo, Hi = hi, Pivot = mid, Axis = axis, Left = left, Right = right };
            return node;
        }

        public int Nearest(Vector3 target)
        {
            int best = -1;
            float bestD = float.MaxValue;
            Search(0, target, ref best, ref bestD);
            return best;
        }

        private void Search(int node, Vector3 target, ref int best, ref float bestD)
        {
            var n = _nodes[node];
            if (n.Axis < 0)
            {
                for (int i = n.Lo; i < n.Hi; i++)
                {
                    float d = Vector3.DistanceSquared(target, _p[_ids[i]]);
                    if (d < bestD) { bestD = d; best = _ids[i]; }
                }
                return;
            }
            int pivotId = _ids[n.Pivot];
            float diff = target[n.Axis] - _p[pivotId][n.Axis];
            int near = diff <= 0 ? n.Left : n.Right;
            int far = diff <= 0 ? n.Right : n.Left;
            Search(near, target, ref best, ref bestD);
            if (diff * diff < bestD) Search(far, target, ref best, ref bestD);
        }
    }
}
