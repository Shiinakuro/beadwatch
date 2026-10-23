using Doudou.Core.Color;
using Doudou.Core.IO;
using Doudou.Core.Recon;
using Doudou.Core.Render;
using Doudou.Core.Services;

// 豆豆工坊命令行：批处理与自测。
//   recon <输出目录> <高度> <方法 auto|hunyuan|hull> front=a.png left=b.png ...
//   render <模型.vox> <输出.png>
Console.OutputEncoding = System.Text.Encoding.UTF8;
var lib = PaletteLibrary.LoadDirectory(AppPaths.Palettes);

// 两张位图的平均通道色差（用 GetPixel，规避 RGBA/BGRA 内部布局差异）。
static double MeanAbsDiff(SkiaSharp.SKBitmap? a, SkiaSharp.SKBitmap? b)
{
    if (a == null || b == null) return double.MaxValue;
    int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
    if (w == 0 || h == 0) return double.MaxValue;
    long sum = 0; int n = 0;
    for (int y = 0; y < h; y += 3)
        for (int x = 0; x < w; x += 3)
        {
            var ca = a.GetPixel(x, y); var cb = b.GetPixel(x, y);
            sum += Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue);
            n += 3;
        }
    return n == 0 ? double.MaxValue : (double)sum / n;
}

if (args.Length == 0)
{
    Console.WriteLine("用法：recon <outdir> <height> <auto|hunyuan|hull> view=path ...");
    return 1;
}

switch (args[0])
{
    case "recon":
    {
        var outDir = args[1];
        Directory.CreateDirectory(outDir);
        int height = int.Parse(args[2]);
        var method = args[3] switch { "hunyuan" => ReconMethod.Hunyuan, "hull" => ReconMethod.VisualHull, _ => ReconMethod.Auto };
        var inputs = new List<(ViewKind, string)>();
        bool ai = false, flat = false, cartoon = false, sym = false;
        int maxColors = 20;
        bool flatten = true;
        var keys = new List<Rgb>();
        var refPhotos = new List<string>();
        string hint = "";
        var shapeOnly = new HashSet<ViewKind>();
        foreach (var a in args.Skip(4))
        {
            if (a == "--ai") { ai = true; continue; }
            if (a == "--flat") { flat = true; continue; }
            if (a == "--cartoon") { cartoon = true; continue; }
            if (a == "--sym") { sym = true; continue; }
            if (a.StartsWith("--colors=")) { maxColors = int.Parse(a[9..]); continue; }
            if (a == "--noflat") { flatten = false; continue; }
            if (a.StartsWith("--keys=")) { keys.AddRange(a[7..].Split(',').Select(Rgb.FromHex)); continue; }
            var kv = a.Split('=', 2);
            if (kv[0] == "ref") { refPhotos.Add(kv[1]); continue; }
            if (kv[0] == "hint") { hint = kv[1]; continue; }
            if (kv[0].EndsWith('!')) { var k = Enum.Parse<ViewKind>(kv[0].TrimEnd('!'), true); shapeOnly.Add(k); inputs.Add((k, kv[1])); continue; }
            inputs.Add((Enum.Parse<ViewKind>(kv[0], true), kv[1]));
        }
        var settings = AppSettings.Load();
        using var hy = new Hy3dClient();
        var pipe = new ReconstructPipeline(lib, hy, new ImageAi(settings));
        var opt = new ReconOptions { Method = method, TargetHeight = height, AiCompleteViews = ai, AiFlatColors = flat, Name = Path.GetFileName(outDir), ReferencePhotos = refPhotos, SubjectHint = hint, ShapeOnlyViews = shapeOnly, CartoonStyle = cartoon, InferSymmetricSides = sym, Quantize = new QuantizeOptions { MaxColors = maxColors }, FlattenPhotos = flatten, KeyColors = keys,
            OnAiImage = (k, p, c) => Console.WriteLine($"  [AI图] {k} {(c ? "缓存" : "新生成")} {p}") };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await pipe.RunAsync(inputs, opt, new Progress<string>(s => Console.WriteLine("  " + s)));
        Console.WriteLine($"完成 {sw.Elapsed.TotalSeconds:F1}s：{res.Model.SX}×{res.Model.SY}×{res.Model.SZ} {res.Model.CountSolid()} 颗 {res.Model.Palette.Count - 1} 色 方法={res.MethodUsed}");
        VoxFile.Write(res.Model, Path.Combine(outDir, "model.vox"));
        foreach (var (yaw, name) in new[] { (35f, "iso_front"), (215f, "iso_back"), (0f, "front"), (90f, "left") })
        {
            using var r = IsoRenderer.Render(IsoRenderer.FromModel(res.Model).ToList(),
                new RenderOptions { Yaw = yaw, Pitch = name.StartsWith("iso") ? 28 : 0, FitSize = 900, Background = SkiaSharp.SKColors.White, Look = BeadLook.Bead });
            IsoRenderer.SavePng(r.Bitmap, Path.Combine(outDir, name + ".png"));
        }
        return 0;
    }
    case "make":
    {
        // make <模型.vox> <输出目录> [壁厚]：空心化 → 面板拆分 → 齿口 → 检查 → 图纸
        var m = VoxFile.Read(args[1]);
        Quantizer.RemapToBrand(m, lib, "mard221");
        var outDir = args[2];
        Directory.CreateDirectory(outDir);
        var h = new Doudou.Core.Edit.EditHistory(m);
        int t = args.Length > 3 ? int.Parse(args[3]) : 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int before = m.CountSolid();
        var hol = Doudou.Core.Ops.Hollow.Apply(m, h, new Doudou.Core.Ops.HollowOptions { Thickness = t });
        Console.WriteLine($"空心化：{before} → {m.CountSolid()} 颗（移除 {hol.Removed}） {sw.ElapsedMilliseconds}ms");
        var (how, flat) = Doudou.Core.Parts.SplitTools.Recommend(m);
        int parts = how == "panel" ? Doudou.Core.Parts.SplitTools.PanelSplit(m, h) : Doudou.Core.Parts.SplitTools.LayerSplit(m, h);
        Console.WriteLine($"拆分（{how}，平面度 {flat:P0}）：{parts} 件 {sw.ElapsedMilliseconds}ms");
        if (how == "panel")
        {
            int joints = Doudou.Core.Parts.SplitTools.FingerJointAll(m, h);
            Console.WriteLine($"齿口：{joints} 对 {sw.ElapsedMilliseconds}ms");
        }
        var rep = Doudou.Core.Parts.PartAnalyzer.Analyze(m);
        Console.WriteLine($"零件 {rep.Parts.Count}，未分配 {rep.Unassigned.Count}，守恒 {rep.Conserved}，错误 {rep.ErrorCount}");
        foreach (var pr in rep.Parts.Where(p => p.Issues.Count > 0).Take(10))
            Console.WriteLine($"  {pr.Name}: {string.Join("; ", pr.Issues.Select(i => i.Message))}");
        var leak = Doudou.Core.Check.LeakChecker.Check(m);
        Console.WriteLine("漏色检查：" + leak.Summary());
        var meta = new ProjectMeta { Title = Path.GetFileNameWithoutExtension(args[1]), Author = "豆豆工坊", Watermark = "豆豆工坊" };
        using var comp = new Doudou.Core.Drawing.DrawingComposer(m, meta, lib);
        Console.WriteLine("装配：" + comp.Plan.Summary + $"（{comp.Plan.Seconds:F2}s）");
        var outp = comp.ExportAll(outDir, new Progress<string>(s => Console.WriteLine("  " + s)));
        ProjectFile.Save(Path.Combine(outDir, "工程.doudou"), m, meta);
        Console.WriteLine($"完成 {sw.ElapsedMilliseconds}ms，文件 {outp.Files.Count}");
        return 0;
    }
    case "hang":
    {
        // hang <主体.vox> <挂件.vox> <输出.vox> <绳长>
        var a = VoxFile.Read(args[1]); var b = VoxFile.Read(args[2]);
        Quantizer.RemapToBrand(a, lib, "mard221"); Quantizer.RemapToBrand(b, lib, "mard221");
        var gold = lib.Get("mard221").Nearest(ColorMath.ToLab(new Rgb(184, 140, 70))).Color;
        var r = Doudou.Core.Ops.Compose.HangBelow(a, b, int.Parse(args[4]), gold.Code, gold.Rgb);
        VoxFile.Write(r, args[3]);
        Console.WriteLine($"组合：{r.SX}×{r.SY}×{r.SZ} {r.CountSolid()} 颗");
        foreach (var (yaw, name) in new[] { (30f, "_iso.png"), (0f, "_front.png") })
        {
            using var rr = IsoRenderer.Render(IsoRenderer.FromModel(r).ToList(), new RenderOptions { Yaw = yaw, Pitch = yaw == 0 ? 0 : 22, FitSize = 1000, Background = SkiaSharp.SKColors.White, Look = BeadLook.Bead });
            IsoRenderer.SavePng(rr.Bitmap, args[3] + name);
        }
        return 0;
    }
    case "selftest":
    {
        int fail = 0;
        void Check(bool ok, string name) { Console.WriteLine((ok ? "  通过 " : "  失败 ") + name); if (!ok) fail++; }
        // 1. 球体：空心化 → 无破洞；挖掉一颗外壳豆 → 检出破洞
        var m = new Doudou.Core.Model.VoxelModel(24, 24, 24);
        var white = m.EnsureColor("H2", new Rgb(255, 255, 255));
        for (int i = 0; i < m.Count; i++) { var p = m.Pos(i); var d = new System.Numerics.Vector3(p.X - 11.5f, p.Y - 11.5f, p.Z - 11.5f); if (d.Length() < 10) m.Colors[i] = white; }
        int solid = m.CountSolid();
        var h = new Doudou.Core.Edit.EditHistory(m);
        Doudou.Core.Ops.Hollow.Apply(m, h, new Doudou.Core.Ops.HollowOptions());
        Check(m.CountSolid() < solid, $"空心化 {solid}→{m.CountSolid()}");
        var leak = Doudou.Core.Check.LeakChecker.Check(m, checkParts: false);
        Check(leak.Count(Doudou.Core.Check.LeakKind.ShellHole) == 0 && leak.Count(Doudou.Core.Check.LeakKind.Floating) == 0, "完整外壳无破洞/悬空：" + leak.Summary());
        h.Undo();
        Check(m.CountSolid() == solid, "撤销空心化");
        h.Redo();
        // 在正面中心挖 2×2 洞
        h.Begin("挖洞");
        for (int z = m.SZ - 1; z >= 0; z--) { int i = m.Index(11, 11, z); if (m.Colors[i] != 0) { h.Set(i, 0, 0); h.Set(m.Index(12, 11, z), 0, 0); h.Set(m.Index(11, 12, z), 0, 0); h.Set(m.Index(12, 12, z), 0, 0); break; } }
        h.Commit();
        leak = Doudou.Core.Check.LeakChecker.Check(m, checkParts: false);
        Check(leak.Count(Doudou.Core.Check.LeakKind.ShellHole) > 0, "外壳挖洞后检出破洞：" + leak.Summary());
        h.Undo();
        // 2. 任意色内芯外露
        var m2 = new Doudou.Core.Model.VoxelModel(10, 10, 10);
        var c2 = m2.EnsureColor("H2", new Rgb(255, 255, 255));
        for (int i = 0; i < m2.Count; i++) { var p = m2.Pos(i); if (p.X > 1 && p.X < 8 && p.Y > 1 && p.Y < 8 && p.Z > 1 && p.Z < 8) m2.Colors[i] = c2; }
        var h2 = new Doudou.Core.Edit.EditHistory(m2);
        Doudou.Core.Ops.Hollow.Apply(m2, h2, new Doudou.Core.Ops.HollowOptions { Interior = Doudou.Core.Ops.InteriorMode.AnyColor });
        Check(Doudou.Core.Check.LeakChecker.Check(m2, checkParts: false).Count(Doudou.Core.Check.LeakKind.CoreExposed) == 0, "任意色内芯被包住");
        h2.Begin("削掉一面"); for (int i = 0; i < m2.Count; i++) if (m2.Pos(i).Z == 7) h2.Set(i, 0, 0); h2.Commit();
        Check(Doudou.Core.Check.LeakChecker.Check(m2, checkParts: false).Count(Doudou.Core.Check.LeakKind.CoreExposed) > 0, "削面后检出内芯外露");
        // 3. 拆分守恒 + 平板 + 装配
        h2.Undo();
        Doudou.Core.Parts.SplitTools.LayerSplit(m2, h2);
        var rep = Doudou.Core.Parts.PartAnalyzer.Analyze(m2);
        Check(rep.Conserved && rep.Unassigned.Count == 0, $"分层拆分守恒（{rep.Parts.Count} 件）");
        Check(rep.Parts.All(p => p.Planar), "所有零件都是单层平板");
        var plan = Doudou.Core.Parts.AssemblyPlanner.Plan(m2, rep);
        Check(plan.ComplexCount == 0 && plan.Steps.Count == rep.Parts.Count, "装配：" + plan.Summary);
        // 4. 面板拆分 + 齿口
        var box = new Doudou.Core.Model.VoxelModel(12, 12, 12);
        var bc = box.EnsureColor("H2", new Rgb(255, 255, 255));
        for (int i = 0; i < box.Count; i++) { var p = box.Pos(i); if (p.X >= 1 && p.X <= 10 && p.Y >= 1 && p.Y <= 10 && p.Z >= 1 && p.Z <= 10) box.Colors[i] = bc; }
        var hb = new Doudou.Core.Edit.EditHistory(box);
        Doudou.Core.Ops.Hollow.Apply(box, hb, new Doudou.Core.Ops.HollowOptions());
        int panels = Doudou.Core.Parts.SplitTools.PanelSplit(box, hb);
        Check(panels == 6, $"空心方盒面板拆分 = 6 块（{panels}）");
        int joints = Doudou.Core.Parts.SplitTools.FingerJointAll(box, hb);
        var rb = Doudou.Core.Parts.PartAnalyzer.Analyze(box);
        Check(joints > 0 && rb.Parts.All(p => p.Planar && p.Islands == 1), $"齿口 {joints} 对，零件仍为连通平板");
        // 5. 工程文件往返
        var tmp = Path.Combine(Path.GetTempPath(), "dd_selftest.doudou");
        ProjectFile.Save(tmp, box, new ProjectMeta { Title = "自测" });
        var (lb, meta, _) = ProjectFile.Load(tmp);
        Check(lb.CountSolid() == box.CountSolid() && lb.PartList.Count == box.PartList.Count && meta.Title == "自测"
              && lb.Colors.SequenceEqual(box.Colors) && lb.Parts.SequenceEqual(box.Parts), "工程文件往返一致");
        // 6. .vox 往返
        var vtmp = Path.Combine(Path.GetTempPath(), "dd_selftest.vox");
        VoxFile.Write(box, vtmp);
        var vb = VoxFile.Read(vtmp);
        Check(vb.CountSolid() == box.CountSolid() && vb.SX == box.SX && vb.SY == box.SY && vb.SZ == box.SZ, ".vox 往返一致");
        // 7. 射线拾取
        var hit = Doudou.Core.Edit.VoxelRay.Cast(box, new System.Numerics.Vector3(6.5f, 6.5f, 40), new System.Numerics.Vector3(0, 0, -1));
        Check(hit is { OnGround: false } hh && hh.Cell == new Doudou.Core.Geometry.Int3(6, 6, 10) && hh.Normal == new Doudou.Core.Geometry.Int3(0, 0, 1), "射线拾取正面");
        // 8. 装配动图 + GIF 编码往返
        var gifRep = Doudou.Core.Parts.PartAnalyzer.Analyze(box);
        var gifPlan = Doudou.Core.Parts.AssemblyPlanner.Plan(box, gifRep);
        var frames = AssemblyAnimation.BuildFrames(box, gifRep, gifPlan, new AssemblyAnimationOptions { FitSize = 200, SubFrames = 2, HoldFrames = 1 });
        Check(frames.Count >= Math.Max(1, gifPlan.Steps.Count) && frames.TrueForAll(f => f.Width == frames[0].Width && f.Height == frames[0].Height),
            $"装配动图 {frames.Count} 帧、尺寸一致 {frames[0].Width}×{frames[0].Height}");
        var gpath = Path.Combine(Path.GetTempPath(), "dd_selftest.gif");
        GifWriter.Save(frames, gpath, 60);
        var gbytes = File.ReadAllBytes(gpath);
        using var dec = SkiaSharp.SKBitmap.Decode(gpath);
        Check(gbytes.Length > 26 && gbytes[0] == 'G' && gbytes[1] == 'I' && gbytes[2] == 'F' && gbytes[^1] == 0x3B
              && dec != null && dec.Width == frames[0].Width && dec.Height == frames[0].Height,
            $"GIF 往返：{gbytes.Length} 字节，解码 {dec?.Width}×{dec?.Height}");
        foreach (var f in frames) f.Dispose();
        Console.WriteLine(fail == 0 ? "全部通过" : $"{fail} 项失败");
        return fail == 0 ? 0 : 2;
    }
    case "gif":
    {
        // gif <模型.vox> <输出.gif> [fit]：按装配顺序生成可循环的拼装动图，并做解码往返校验
        var m = VoxFile.Read(args[1]);
        int fit = args.Length > 3 ? int.Parse(args[3]) : 560;
        var rep = Doudou.Core.Parts.PartAnalyzer.Analyze(m);
        if (rep.Parts.Count == 0)
        {
            // .vox 不带零件信息：现场拆一次件，让动图命令对任意模型可用
            var h = new Doudou.Core.Edit.EditHistory(m);
            var (how, _) = Doudou.Core.Parts.SplitTools.Recommend(m);
            if (how == "panel") Doudou.Core.Parts.SplitTools.PanelSplit(m, h); else Doudou.Core.Parts.SplitTools.LayerSplit(m, h);
            rep = Doudou.Core.Parts.PartAnalyzer.Analyze(m);
        }
        if (rep.Parts.Count == 0) { Console.WriteLine("模型还没有拆分零件，无法生成装配动图"); return 1; }
        var plan = Doudou.Core.Parts.AssemblyPlanner.Plan(m, rep);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frames = AssemblyAnimation.BuildFrames(m, rep, plan, new AssemblyAnimationOptions { FitSize = fit });
        GifWriter.Save(frames, args[2], 100);
        using var dec = SkiaSharp.SKBitmap.Decode(args[2]);
        double mad = MeanAbsDiff(frames[0], dec);
        Console.WriteLine($"{frames.Count} 帧 → {args[2]}（{new FileInfo(args[2]).Length / 1024} KB），解码 {dec?.Width}×{dec?.Height}，首帧平均色差 {mad:F1}，{sw.ElapsedMilliseconds}ms");
        foreach (var f in frames) f.Dispose();
        return dec != null && mad < 20 ? 0 : 3;
    }
    case "sil":
    {
        // sil <图片> <输出.png>：导出识别出的前景（前景保留原色，背景涂蓝）
        var img = Doudou.Core.Imaging.RgbaImage.Load(args[1]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sil = Doudou.Core.Imaging.Silhouette.FromImage(img);
        var o = new Doudou.Core.Imaging.RgbaImage(img.W, img.H, (byte[])img.Px.Clone());
        for (int i = 0; i < img.W * img.H; i++) if (!sil.Mask[i]) { o.Px[i * 4] = 40; o.Px[i * 4 + 1] = 90; o.Px[i * 4 + 2] = 160; o.Px[i * 4 + 3] = 255; } else o.Px[i * 4 + 3] = 255;
        o.SavePng(args[2]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: 色键={Doudou.Core.Imaging.BackgroundRemoval.DetectChroma(img)} 棋盘格={(Doudou.Core.Imaging.BackgroundRemoval.DetectChecker(img) is { } c ? c.Size + "px" : "无")} 前景 {sil.Mask.Count(b => b) * 100.0 / sil.Mask.Length:F1}% {sw.ElapsedMilliseconds}ms");
        return 0;
    }
    case "flat":
    {
        // flat <图片> <输出.png> [主色,...]：去光影平涂化
        var img = Doudou.Core.Imaging.RgbaImage.Load(args[1]);
        var sil = Doudou.Core.Imaging.Silhouette.FromImage(img);
        var ks = args.Length > 3 ? args[3].Split(',').Select(Rgb.FromHex).ToList() : null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Doudou.Core.Imaging.ColorFlatten.Flatten(img, sil, 24, ks);
        for (int i = 0; i < img.W * img.H; i++) if (!sil.Mask[i]) { r.Image.Px[i * 4] = 40; r.Image.Px[i * 4 + 1] = 90; r.Image.Px[i * 4 + 2] = 160; r.Image.Px[i * 4 + 3] = 255; }
        r.Image.SavePng(args[2]);
        Console.WriteLine($"{sw.ElapsedMilliseconds}ms，{r.Colors.Count} 色：" + string.Join(" ", r.Colors.Select(c => c.Color.Hex + "×" + c.Pixels)));
        var auto = Doudou.Core.Imaging.ColorFlatten.ExtractKeyColors(new[] { (img, sil) });
        Console.WriteLine("自动主色：" + string.Join(",", auto.Select(c => c.Hex)));
        return 0;
    }
    case "render":
    {
        var m = VoxFile.Read(args[1]);
        using var r = IsoRenderer.Render(IsoRenderer.FromModel(m).ToList(), new RenderOptions { FitSize = 1000, Background = SkiaSharp.SKColors.White });
        IsoRenderer.SavePng(r.Bitmap, args[2]);
        return 0;
    }
}
Console.WriteLine("未知命令");
return 1;
