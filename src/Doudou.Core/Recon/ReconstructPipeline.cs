using Doudou.Core.Color;
using Doudou.Core.Imaging;
using Doudou.Core.IO;
using Doudou.Core.Model;
using Doudou.Core.Services;

namespace Doudou.Core.Recon;

public enum ReconMethod { Auto, Hunyuan, VisualHull }

public sealed class ReconOptions
{
    public ReconMethod Method { get; set; } = ReconMethod.Auto;
    /// <summary>成品高度（豆 / 层）。真人图纸常见 30~60。</summary>
    public int TargetHeight { get; set; } = 40;
    public bool AiCompleteViews { get; set; } = true;
    public bool AiFlatColors { get; set; }
    public string SubjectHint { get; set; } = "";
    public QuantizeOptions Quantize { get; set; } = new();
    public int Seed { get; set; } = 1234;
    /// <summary>混元生成最多尝试次数；平均轮廓吻合度达到 AcceptFit 即停止。</summary>
    public int MaxAttempts { get; set; } = 3;
    public double AcceptFit { get; set; } = 0.75;
    public string Name { get; set; } = "未命名";
    /// <summary>
    /// 随手拍的参考照片（角度、背景不规范）。给出时先由 AI 生成标准正视图，再以它为基准生成背/左/右视图，
    /// 保证四张视图风格、比例一致。
    /// </summary>
    public List<string> ReferencePhotos { get; set; } = new();
    /// <summary>参考照片模式下先卡通化（大块形体、平涂色），细碎结构更容易在拼豆分辨率下辨认。</summary>
    public bool CartoonStyle { get; set; }
    /// <summary>近似回转对称的物体：缺少侧面/背面时由正面轮廓推断形状（只约束形状）。</summary>
    public bool InferSymmetricSides { get; set; }
    /// <summary>只用于约束形状、不参与取色的视图。</summary>
    public HashSet<ViewKind> ShapeOnlyViews { get; set; } = new();
    /// <summary>照片去光影：实拍照片取色前先平涂化（AI 生成的卡通图本来就平涂，不处理）。</summary>
    public bool FlattenPhotos { get; set; } = true;
    /// <summary>用户确认的主色：取色时只在这些颜色里选（再映射到品牌色号）。为空 = 不限制。</summary>
    public List<Rgb> KeyColors { get; set; } = new();
    /// <summary>这些输入视图本身就是 AI 生成的（例如从工程里复用），不做去光影。</summary>
    public HashSet<ViewKind> AiViews { get; set; } = new();
    /// <summary>每拿到一张 AI 图回调一次：(视图名, 文件路径, 是否来自缓存)。</summary>
    public Action<string, string, bool>? OnAiImage { get; set; }
}

public sealed class ReconResult
{
    public VoxelModel Model { get; init; } = null!;
    public List<ViewImage> Views { get; init; } = new();
    public string? MeshPath { get; init; }
    public ReconMethod MethodUsed { get; init; }
    public Dictionary<ViewKind, double> Fit { get; init; } = new();
    public List<string> Log { get; init; } = new();
    public string WorkDir { get; init; } = "";
}

/// <summary>3~6 张图 → 立体拼豆模型 的完整流程。</summary>
public sealed class ReconstructPipeline
{
    private readonly PaletteLibrary _lib;
    private readonly Hy3dClient? _hy3d;
    private readonly ImageAi? _ai;

    public ReconstructPipeline(PaletteLibrary lib, Hy3dClient? hy3d, ImageAi? ai)
    {
        _lib = lib; _hy3d = hy3d; _ai = ai;
    }

    public async Task<ReconResult> RunAsync(IReadOnlyList<(ViewKind Kind, string Path)> inputs, ReconOptions opt,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0 && opt.ReferencePhotos.Count == 0) throw new ArgumentException("请至少提供一张图片");
        var log = new List<string>();
        void Say(string s) { log.Add(s); progress?.Report(s); }
        var work = AppPaths.NewWorkDir("recon");

        // 统一的 AI 调用：先查缓存（同一组参考图 + 同样设置 → 直接复用，不花额度）
        async Task<string> Ai(IReadOnlyList<string> refs, string prompt, string outp, string label)
        {
            bool cached = !_ai!.ForceRegenerate && _ai.CachedPath(refs, prompt) != null;
            Say(cached ? $"复用已生成的 AI {label}（不消耗额度）" : $"AI 生成{label}…");
            await _ai.EditAsync(refs, prompt, outp, ct);
            opt.OnAiImage?.Invoke(label, outp, cached);
            return outp;
        }

        // 1. 读图 + 抠轮廓
        var views = new List<ViewImage>();
        foreach (var (kind, path) in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var v = ViewImage.Load(kind, path);
            v.ShapeOnly = opt.ShapeOnlyViews.Contains(kind);
            v.IsAi = opt.AiViews.Contains(kind);
            if (v.Sil.Empty) throw new InvalidOperationException($"{ViewGeometry.Chinese(kind)}视图没有识别到主体：{Path.GetFileName(path)}");
            views.Add(v);
            Say($"读取{ViewGeometry.Chinese(kind)}视图 {v.Image.W}×{v.Image.H}，主体 {v.Sil.BoxW}×{v.Sil.BoxH}");
        }

        // 1b. 参考照片 → AI 标准视图（先正面，再以正面为基准出其余三面）
        if (opt.ReferencePhotos.Count > 0)
        {
            if (_ai is not { Configured: true }) throw new InvalidOperationException("使用参考照片需要配置图像 API");
            var refs = opt.ReferencePhotos.Take(4).ToList();
            if (!views.Any(v => v.Kind == ViewKind.Front))
            {
                var fp = Path.Combine(work, "ai_front.png");
                await Ai(refs, opt.CartoonStyle ? ImageAi.CartoonFrontPrompt(opt.SubjectHint) : ImageAi.ViewPrompt(ViewKind.Front, opt.SubjectHint), fp, "正视图");
                views.Add(Mark(ViewImage.Load(ViewKind.Front, fp)));
            }
            var front = views.First(v => v.Kind == ViewKind.Front).SourcePath;
            var others = new[] { ViewKind.Left, ViewKind.Back, ViewKind.Right }.Where(k => views.All(v => v.Kind != k)).ToList();
            var jobs = others.Select(async k =>
            {
                var outp = Path.Combine(work, $"ai_{k.ToString().ToLowerInvariant()}.png");
                await Ai(new[] { front }.Concat(refs).Take(4).ToList(),
                    opt.CartoonStyle ? ImageAi.CartoonViewPrompt(k) : "The FIRST image is the authoritative front view. " + ImageAi.ViewPrompt(k, opt.SubjectHint), outp, ViewGeometry.Chinese(k) + "视图");
                return Mark(ViewImage.Load(k, outp));
            }).ToList();
            foreach (var j in jobs)
            {
                try { var v = await j; if (!v.Sil.Empty) views.Add(v); }
                catch (Exception e) when (e is not OperationCanceledException) { Say("AI 视图生成失败（继续）：" + e.Message); }
            }
        }

        // 2. AI 补全缺失的四个水平视图
        if (opt.AiCompleteViews && opt.ReferencePhotos.Count == 0 && _ai is { Configured: true })
        {
            var have = views.Select(v => v.Kind).ToHashSet();
            var refs = inputs.Select(i => i.Path).Take(4).ToList();
            var missing = new[] { ViewKind.Front, ViewKind.Left, ViewKind.Back, ViewKind.Right }.Where(k => !have.Contains(k)).ToList();
            var tasks = missing.Select(async k =>
            {
                var outp = Path.Combine(work, $"ai_{k.ToString().ToLowerInvariant()}.png");
                await Ai(refs, ImageAi.ViewPrompt(k, opt.SubjectHint), outp, ViewGeometry.Chinese(k) + "视图");
                return Mark(ViewImage.Load(k, outp));
            }).ToList();
            foreach (var t in tasks)
            {
                try
                {
                    var v = await t;
                    if (!v.Sil.Empty) views.Add(v);
                }
                catch (Exception e) when (e is not OperationCanceledException) { Say("AI 补全失败（继续）：" + e.Message); }
            }
        }

        // 2b. 对称推断侧面形状
        if (opt.InferSymmetricSides && views.FirstOrDefault(v => v.Kind == ViewKind.Front && !v.ShapeOnly) is { } fv)
        {
            foreach (var (k, p) in SymmetryViews.Infer(fv, work))
            {
                if (views.Any(v => v.Kind == k)) continue;
                var sv = ViewImage.Load(k, p);
                sv.ShapeOnly = true;
                views.Add(sv);
                Say($"由正面轮廓推断{ViewGeometry.Chinese(k)}形状");
            }
        }

        // 3. 形状
        Occupancy? occ = null;
        string? meshPath = null;
        var method = opt.Method;
        if (method != ReconMethod.VisualHull && _hy3d != null && Hy3dClient.IsInstalled(out var why))
        {
            try
            {
                var hv = new Dictionary<string, string>();
                foreach (var v in views)
                {
                    var key = ViewGeometry.HunyuanKey(v.Kind);
                    if (key == null || hv.ContainsKey(key)) continue;
                    var p = Path.Combine(work, $"hy_{key}.png");
                    CutoutSquare(v).SavePng(p);
                    hv[key] = p;
                }
                if (!hv.ContainsKey("front")) throw new InvalidOperationException("混元3D 需要正面视图");
                // 扩散模型有随机性：轮廓吻合度不够时换随机种子重试，取最好的一次
                double bestFit = -1;
                for (int attempt = 0; attempt < Math.Max(1, opt.MaxAttempts); attempt++)
                {
                    int seed = opt.Seed + attempt * 7919;
                    var outObj = Path.Combine(work, $"shape_{attempt}.obj");
                    await _hy3d.GenerateAsync(hv, outObj, seed: seed, progress: progress, ct: ct);
                    Say($"体素化网格（第 {attempt + 1} 次）…");
                    var mesh = MeshLoader.Load(outObj);
                    var raw = Voxelizer.Voxelize(mesh, opt.TargetHeight);
                    var cand = BestOrientation(raw, views, Say);
                    double avgFit = views.Average(v => SilhouetteFit.IoU(cand, v));
                    Say($"轮廓平均吻合度 {avgFit:P0}");
                    if (avgFit > bestFit) { bestFit = avgFit; occ = cand; meshPath = outObj; }
                    if (bestFit >= opt.AcceptFit) break;
                    if (attempt + 1 < opt.MaxAttempts) Say("吻合度偏低，换随机种子重试…");
                }
                method = ReconMethod.Hunyuan;
            }
            catch (Exception e) when (e is not OperationCanceledException && opt.Method == ReconMethod.Auto)
            {
                Say("混元3D 不可用，改用轮廓雕刻：" + e.Message);
            }
        }
        else if (method == ReconMethod.Hunyuan)
            throw new InvalidOperationException("混元3D 未安装：" + (Hy3dClient.IsInstalled(out var w2) ? "" : w2));

        if (occ == null)
        {
            Say("轮廓雕刻（Visual Hull）…");
            occ = VisualHull.Carve(views, opt.TargetHeight);
            method = ReconMethod.VisualHull;
        }
        Say($"形状：{occ.SX}×{occ.SY}×{occ.SZ}，{occ.Count()} 颗");

        // 4. 可选：AI 平涂取色图
        if (opt.AiFlatColors && _ai is { Configured: true })
        {
            foreach (var v in views.Where(v => !v.ShapeOnly && !v.IsAi))
            {
                try
                {
                    var outp = Path.Combine(work, $"flat_{v.Kind.ToString().ToLowerInvariant()}.png");
                    await Ai(new[] { v.SourcePath }, ImageAi.FlatColorPrompt, outp, ViewGeometry.Chinese(v.Kind) + "平涂图");
                    var f = ViewImage.Load(v.Kind, outp);
                    if (!f.Sil.Empty) v.ColorSource = f;
                }
                catch (Exception e) when (e is not OperationCanceledException) { Say("平涂化失败（使用原图取色）：" + e.Message); }
            }
        }

        // 4b. 本地去光影：实拍照片（非 AI 图、且没有 AI 平涂图）先平涂化再取色
        if (opt.FlattenPhotos)
            foreach (var v in views.Where(v => !v.ShapeOnly && !v.IsAi && v.ColorSource == null))
            {
                ct.ThrowIfCancellationRequested();
                Say($"去光影：{ViewGeometry.Chinese(v.Kind)}视图…");
                v.FlattenWith(opt.KeyColors);
            }

        // 5. 上色 + 量化
        Say("多视图投影上色…");
        var proj = ColorProjector.Project(occ, views);
        Say($"直接取色 {proj.SeenVoxels} 颗，继承 {proj.InheritedVoxels} 颗");
        if (opt.KeyColors.Count > 0)
        {
            // 只在主色里取色：每颗豆归到最接近的主色（对明暗不敏感），之后主色再映射到色号
            var keys = opt.KeyColors.Select(ColorMath.ToLab).ToList();
            for (int i = 0; i < proj.Colors.Length; i++)
                if (proj.Colors[i] is { } c) proj.Colors[i] = keys[ColorFlatten.NearestKey(c, keys)];
            opt.Quantize.MaxColors = Math.Max(opt.Quantize.MaxColors, opt.KeyColors.Count);
            Say($"按 {opt.KeyColors.Count} 种主色取色");
        }
        var model = Quantizer.Build(occ, proj.Colors, _lib, opt.Quantize, opt.Name);
        int cleaned = SurfaceColorCleaner.Clean(model);
        if (cleaned > 0)
        {
            model.CompactPalette();
            Say($"表面颜色清理 {cleaned} 颗");
        }
        Say($"量化到 {_lib.Get(opt.Quantize.BrandId).Title}：{model.Palette.Count - 1} 色");

        var fit = views.ToDictionary(v => v.Kind, v => SilhouetteFit.IoU(occ, v));
        foreach (var (k, iou) in fit) Say($"{ViewGeometry.Chinese(k)}轮廓吻合度 {iou:P0}");

        return new ReconResult { Model = model, Views = views, MeshPath = meshPath, MethodUsed = method, Fit = fit, Log = log, WorkDir = work };
    }

    private static ViewImage Mark(ViewImage v) { v.IsAi = true; return v; }

    /// <summary>混元输出的朝向不一定和我们的约定一致：试 8 种（4 个朝向 × 是否镜像），取轮廓吻合度最高的。</summary>
    public static Occupancy BestOrientation(Occupancy raw, IReadOnlyList<ViewImage> views, Action<string>? say = null)
    {
        Occupancy best = raw; double bestScore = -1; string bestName = "";
        for (int mirror = 0; mirror < 2; mirror++)
            for (int k = 0; k < 4; k++)
            {
                var cand = raw.Transformed(k, mirror == 1);
                double score = 0;
                foreach (var v in views) score += SilhouetteFit.IoU(cand, v) * (v.Kind == ViewKind.Front ? 1.5 : 1);
                if (score > bestScore) { bestScore = score; best = cand; bestName = $"旋转 {k * 90}°{(mirror == 1 ? " + 镜像" : "")}"; }
            }
        say?.Invoke($"自动对齐朝向：{bestName}");
        return best;
    }

    /// <summary>抠出主体，居中贴到正方形透明画布（四周留 10%），供混元使用。</summary>
    public static RgbaImage CutoutSquare(ViewImage v)
    {
        var cut = v.Sil.ApplyTo(v.Image);
        int side = (int)(Math.Max(v.Sil.BoxW, v.Sil.BoxH) * 1.2);
        int x0 = v.Sil.X0 - (side - v.Sil.BoxW) / 2, y0 = v.Sil.Y0 - (side - v.Sil.BoxH) / 2;
        var sq = cut.Crop(x0, y0, side, side);
        return side > 1024 ? sq.Resized(1024, 1024) : sq;
    }
}
