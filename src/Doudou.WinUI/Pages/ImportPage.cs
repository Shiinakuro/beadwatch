using Doudou.Core.Color;
using Doudou.Core.Imaging;
using Doudou.Core.IO;
using Doudou.Core.Recon;
using Doudou.Core.Services;
using Doudou.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>① 图片导入 + 三维重建。</summary>
public sealed class ImportPage : IStepPage
{
    public string Header => "图片 / 模型导入";
    public string Description => "上传多视图图片，或导入 OBJ/GLB/STL 模型，选好豆高后体素化";
    public UIElement View { get; }

    private sealed class Item
    {
        public string Path = "";
        public int Role;
        public (int X0, int Y0, int X1, int Y1)? Box;
        /// <summary>这张图本身就是 AI 生成的视图（从工程恢复），不再做去光影。</summary>
        public bool IsAi;
        /// <summary>从工程恢复的预处理视图（可能是透明底抠图），不是本次 AI 新生成。</summary>
        public bool Restored;
        public int W, H;
        /// <summary>缩略图（与框选同一套像素：已按 EXIF 转正）。</summary>
        public WriteableBitmap? Thumb;
    }

    private static readonly string[] Roles = { "正面", "背面", "左侧（面朝左）", "右侧（面朝右）", "顶部", "底部", "参考照片" };

    private readonly IHost _h;
    private readonly List<Item> _items = new();
    private readonly ItemsWrap _tiles = new() { HSpacing = 8, VSpacing = 8 };
    private readonly TextBox _name = new() { Text = "我的作品" };
    private readonly TextBox _hint = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 60, PlaceholderText = "可选：一句话描述物体，帮助 AI 理解。例如：粉色礼服小王子手办，戴粉色高帽，红色短发，蓝眼睛，斜挎珍珠包" };
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono,Consolas,Microsoft YaHei UI") };
    private readonly ItemsWrap _aiThumbs = new() { HSpacing = 6, VSpacing = 6 };
    private readonly TextBlock _aiState = Caption("");
    private readonly TextBlock _sizeHint = Caption("");
    private readonly TextBlock _gpuNote = Caption("");
    private readonly TextBlock _hyState = Caption("");
    private readonly ItemsWrap _keySwatches = new() { HSpacing = 4, VSpacing = 4 };
    private readonly List<Rgb> _keyColors = new();
    private CheckBox _onlyKeysCheck = null!;
    private int _height = 100, _method, _brand, _maxColors = 16;
    private bool _cartoon = true, _complete = true, _flat, _sym, _flatten = true, _onlyKeys, _force, _askBox = true;
    private readonly List<string> _brandIds;

    public ImportPage(IHost h)
    {
        _h = h;
        _brandIds = h.S.Lib.Brands.Keys.OrderBy(k => k).ToList();
        _brand = Math.Max(0, _brandIds.IndexOf(h.S.Settings.DefaultBrand));
        var root = new StackPanel();

        var drop = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 18, 12, 18),
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Icon(G.Photo, 26),
                    new TextBlock { Text = "点击添加图片，或直接拖到窗口里", HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "添加后会逐张弹出预览，框选主体（可跳过）", FontSize = 12, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        drop.Click += async (_, _) => await Browse();
        root.Children.Add(Card("图片", "标准视图（正/背/左/右）效果最好；角度随意的实拍照片标为「参考照片」，由 AI 先整理成标准视图。",
            drop, Check("添加图片后自动弹出框选主体", _askBox, v => _askBox = v), _tiles));

        root.Children.Add(Card("三维模型", "已有 OBJ / GLB / STL / VOX 可以直接导入，不用照片。GLB/OBJ 带纹理会自动按纹理上色；STL 不带颜色，用上面添加的图片做参考上色；VOX 按 MagicaVoxel 标准转正。",
            Flow(Btn("导入 OBJ / GLB / STL / VOX", async () => await ImportMeshAsync(), G.Download, accent: true,
                tip: "按当前「成品高度」体素化；GLB/OBJ 自带纹理会保留五官细节")),
            Caption("导入前会检查朝向：有参考图时用轮廓吻合度选最正方向，没有参考图时按最长轴自动转正。没有纹理也没有参考图时生成单色模型。")));

        root.Children.Add(Card("作品", null, Row("名称", _name), _hint));

        var height = SliderRow("成品高度", 16, 128, _height, v => { _height = (int)v; UpdateSize(); }, 1, "0", " 颗");
        root.Children.Add(Card("重建设置", null,
            _gpuNote, _hyState,
            height.Row, _sizeHint,
            Row("建模方式", Combo(new[] { "自动（本地混元3D / 在线 API / 轮廓）", "本地混元3D（需要 NVIDIA 显卡）", "在线混元3D 3.1（需要 API Key）", "轮廓雕刻（纯 CPU）" }, 0, i => _method = i)),
            Row("色卡", Combo(_brandIds.Select(id => h.S.Lib.Brands[id].Title), _brand, i => _brand = i)),
            SliderRow("最多颜色", 4, 40, _maxColors, v => _maxColors = (int)v, 1, "0", " 色").Row));

        root.Children.Add(Card("取色", "实拍照片的明暗、反光会让颜色很脏。去光影会把同一种材质归并成一块纯色；再指定主色，模型就只会用这些颜色。",
            Check("去光影（实拍照片推荐）", _flatten, v => _flatten = v),
            _onlyKeysCheck = Check("只用下面的主色取色", _onlyKeys, v => _onlyKeys = v),
            _keySwatches,
            Flow(Btn("自动提取主色", async () => await ExtractKeys(), G.Magic, tip: "从框选后的图片自动提取主要颜色"),
                 Btn("清空", () => { _keyColors.Clear(); RefreshKeys(); }, G.Delete)),
            Caption("在框选对话框里打开「吸色」，点击眼睛、皮肤、头发等关键部位，可以手动补充主色；点色块可删除。")));

        root.Children.Add(Card("AI 辅助", "可选。用 OpenAI 兼容的图像模型整理照片、补全视角与平涂取色；设置里填好地址和 Key 后会自动获取模型列表。",
            _aiState,
            Check("卡通化：细节放大、色块平涂（推荐，辨识度最高）", _cartoon, v => _cartoon = v),
            Check("补全缺失的正/背/左/右视图", _complete, v => _complete = v),
            Check("AI 平涂取色（只影响颜色，消耗额度）", _flat, v => _flat = v),
            Check("近似回转对称（铃铛、瓶罐）：由正面推断侧面形状", _sym, v => _sym = v),
            Check("重新生成 AI 图（默认复用已生成的，不消耗额度）", _force, v => _force = v)));

        root.Children.Add(WideBtn("开始生成立体模型", async () => await Run(), G.Magic));

        var logBody = new StackPanel { Spacing = 8 };
        logBody.Children.Add(new TextBlock { Text = "AI 图 / 工程视图（点击放大）", FontSize = 12, Opacity = 0.7 });
        logBody.Children.Add(new TextBlock
        {
            Text = "「缓存复用」= 本地 AI 缓存；「工程视图」= 从工程恢复的预处理视图，透明底属于正常抠图。效果不好可勾选「重新生成 AI 图」。",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text3),
        });
        logBody.Children.Add(_aiThumbs);
        logBody.Children.Add(_log);
        var exp = new Expander
        {
            Header = "过程日志", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0), Content = logBody,
        };
        root.Children.Add(exp);
        View = root;
        UpdateSize();
        Refresh();
        RefreshKeys();
        h.S.ModelReplaced += () => h.View.DispatcherQueue.TryEnqueue(RestoreFromProject);
    }

    public void OnEnter()
    {
        var ai = new ImageAi(_h.S.Settings);
        _aiState.Text = ai.Configured
            ? $"已连接 · {_h.S.Settings.ImageModel}。生图 API 只整理照片、补全视角和平涂取色，不直接画豆图。同图同设置会复用缓存，只有新组合才消耗额度（约 ¥0.15/张）。"
            : ai.HasEndpoint
                ? "已填写生图服务，但还没有选择模型：到「设置 → AI 图像服务」点「获取模型」或手动填写模型名。"
                : "生图 API 是可选的：把实拍整理成标准视图、补全缺失角度、生成平涂取色参考，最后仍由本地重建和色卡量化出图。到「设置 → AI 图像服务」填入地址和 Key 后会自动获取模型列表。";
        _aiState.Text += Hy3dClient.CutoutInstalled
            ? "\n本地抠图模型就绪：框选后会自动去掉墙面、桌面、水印等背景。"
            : "\n本地抠图模型未安装：框选后按背景色去背景。可在「设置 → 本地 AI 模型」一键安装。";

        var local = Hy3dClient.IsInstalled(out var why);
        _gpuNote.Text = GpuProbe.LocalChoiceText + (local ? " 本地 2mv 权重已就绪。" : " 本地 2mv 未安装：" + why + "。");
        var api = new Hunyuan3dApi(_h.S.Settings);
        _hyState.Text = api.Configured
            ? "在线混元 3.1 已配置：显存不足或本地模型未安装时，「自动」会优先用它；单图约 20 积分/次，照片会上传到腾讯云。在线接口当前使用正面图（单图），多视图请用本地 2mv。"
            : "在线混元 3.1 未配置：到「设置 → 混元 3D 在线 API」填 Key；不配置时自动退回轮廓雕刻。";
    }

    /// <summary>打开工程时：把工程里保存的视图图片（多为 AI 生成）放回列表，直接复用，不再调用 AI。</summary>
    private bool _running;

    private void RestoreFromProject()
    {
        if (_running) return;   // 本页自己生成的模型，不要用结果覆盖用户的图片列表
        var files = _h.S.ViewFiles;
        if (files.Count == 0) return;
        _items.Clear();
        _aiThumbs.Children.Clear();   // 否则每打开一个工程就把上一次的缩略图叠在后面
        foreach (var (kind, path) in files.OrderBy(kv => kv.Key))
            if (File.Exists(path)) _items.Add(new Item { Path = path, Role = (int)kind, IsAi = true, Restored = true });
        _name.Text = _h.S.Meta.Title;
        Refresh();
        Log($"已从工程恢复 {_items.Count} 张视图图片：重新生成模型时直接使用，不会再调用 AI。");
        foreach (var it in _items) AddThumb(Roles[it.Role], it.Path, true, project: true);
    }

    private void UpdateSize()
    {
        double cm = _height * _h.S.Settings.BeadPitchMm / 10;
        _sizeHint.Text = $"约 {cm:F1} cm 高。人物公仔常用 40~64 颗，细节多的挂件 80~100 颗（零件也会更多）。";
    }

    private async Task Browse()
    {
        var p = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, _h.Hwnd);
        foreach (var e in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) p.FileTypeFilter.Add(e);
        var files = await p.PickMultipleFilesAsync();
        if (files.Count > 0) AddImages(files.Select(f => f.Path));
    }

    public async void AddImages(IEnumerable<string> files)
    {
        var added = new List<Item>();
        foreach (var f in files)
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            int role = n.Contains("front") || n.Contains("正") ? 0 : n.Contains("back") || n.Contains("背") ? 1
                : n.Contains("left") || n.Contains("左") ? 2 : n.Contains("right") || n.Contains("右") ? 3
                : n.Contains("top") || n.Contains("顶") ? 4 : n.Contains("photo") || n.Contains("照") ? 6 : -1;
            if (role < 0)
            {
                var used = _items.Select(i => i.Role).ToHashSet();
                role = new[] { 0, 1, 2, 3 }.FirstOrDefault(r => !used.Contains(r), 6);
            }
            var it = new Item { Path = f, Role = role };
            _items.Add(it);
            added.Add(it);
        }
        if (_items.Count > 0 && (_name.Text == "我的作品" || string.IsNullOrWhiteSpace(_name.Text)))
            _name.Text = System.IO.Path.GetFileNameWithoutExtension(_items[0].Path);
        Refresh();
        if (!_askBox) return;
        for (int k = 0; k < added.Count; k++)
        {
            var dlg = await EditBox(added[k], k + 1, added.Count);
            if (dlg?.SkipRest == true) break;
        }
    }

    private async Task<SubjectDialog?> EditBox(Item it, int index = 1, int total = 1)
    {
        try
        {
            var dlg = new SubjectDialog(it.Path, it.Box, _keyColors);
            var r = await dlg.ShowAsync(_h.XamlRoot, index, total);
            if (r == ContentDialogResult.None) return dlg;
            it.Box = dlg.Box;
            _keyColors.Clear();
            _keyColors.AddRange(dlg.PickedColors);
            RefreshKeys();
            Refresh();
            return dlg;
        }
        catch (Exception e)
        {
            _h.Notify("打开图片失败：" + e.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    private void Refresh()
    {
        _tiles.Children.Clear();
        foreach (var it in _items.ToList())
        {
            var img = new Image { Stretch = Stretch.Uniform };
            var overlay = new Canvas { IsHitTestVisible = false };
            var pic = new Grid { Width = 148, Height = 112, Background = it.IsAi ? Solid(0xFF, 0x00, 0xFF) : Solid(0x14, 0x1A, 0x26), CornerRadius = new CornerRadius(4) };
            pic.Children.Add(img);
            pic.Children.Add(overlay);
            var del = new Button { Content = Icon(G.Delete, 12), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
            del.Click += (_, _) => { _items.Remove(it); Refresh(); };
            pic.Children.Add(del);
            if (it.IsAi) pic.Children.Add(new Border { Child = Theme.Tag(it.Restored ? "工程" : "AI", it.Restored ? Theme.Green : Theme.Blue, 10, Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) });
            var role = Combo(Roles, it.Role, r => it.Role = r);
            role.Width = 148;
            var box = new Button { Content = Label(it.Box == null ? "框选主体" : "已框选 · 调整", G.Crop), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, Padding = new Thickness(6, 3, 6, 3) };
            box.Click += async (_, _) => await EditBox(it);
            var tile = new StackPanel { Spacing = 4, Width = 148 };
            tile.Children.Add(pic);
            tile.Children.Add(role);
            tile.Children.Add(box);
            ToolTipService.SetToolTip(tile, System.IO.Path.GetFileName(it.Path));
            _tiles.Children.Add(tile);
            try
            {
                if (it.Thumb == null)
                {
                    var full = RgbaImage.Load(it.Path);
                    it.W = full.W; it.H = full.H;
                    double k = Math.Min(1, 300.0 / Math.Max(full.W, full.H));
                    it.Thumb = SubjectDialog.ToBitmap(k < 1 ? full.Resized(Math.Max(1, (int)(full.W * k)), Math.Max(1, (int)(full.H * k))) : full);
                }
                img.Source = it.Thumb;
                if (it.Box is { } bx && it.W > 0)
                {
                    // 缩略图上画出框选范围
                    double s = Math.Min(148.0 / it.W, 112.0 / it.H), ox = (148 - it.W * s) / 2, oy = (112 - it.H * s) / 2;
                    var r = new Rectangle { Stroke = Theme.B(Theme.Orange), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, Width = (bx.X1 - bx.X0) * s, Height = (bx.Y1 - bx.Y0) * s };
                    Canvas.SetLeft(r, ox + bx.X0 * s); Canvas.SetTop(r, oy + bx.Y0 * s);
                    overlay.Children.Add(r);
                }
            }
            catch (Exception) { }
        }
    }

    private void RefreshKeys()
    {
        _keySwatches.Children.Clear();
        foreach (var c in _keyColors.ToList())
        {
            var b = new Button { Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0), Content = Swatch(c, 26) };
            ToolTipService.SetToolTip(b, c.Hex + "（点击移除）");
            b.Click += (_, _) => { _keyColors.Remove(c); RefreshKeys(); };
            _keySwatches.Children.Add(b);
        }
        if (_keyColors.Count == 0) _keySwatches.Children.Add(Caption("还没有主色"));
    }

    private void Log(string s) => _h.View.DispatcherQueue.TryEnqueue(() => _log.Text += s + "\n");

    private void AddThumb(string label, string path, bool cached, bool project = false)
    {
        _h.View.DispatcherQueue.TryEnqueue(async () =>
        {
            var img = new Image { Width = 84, Height = 84, Stretch = Stretch.Uniform };
            try
            {
                var b = new BitmapImage { DecodePixelWidth = 168 };
                using var fs = File.OpenRead(path);
                await b.SetSourceAsync(fs.AsRandomAccessStream());
                img.Source = b;
            }
            catch (Exception) { return; }
            // 工程视图可能是透明底抠图；垫品红键底，避免在深色面板上看起来像缺块。
            var g = new Grid { Background = Solid(0xFF, 0x00, 0xFF), CornerRadius = new CornerRadius(4) };
            g.Children.Add(img);
            g.Children.Add(new Border
            {
                Child = new TextBlock { Text = project ? "工程视图" : cached ? "缓存复用" : "新生成", FontSize = 10 },
                Background = Theme.B(project ? Theme.Blue : cached ? Theme.Green : Theme.Orange, 0xCC), Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            });
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 });
            var btn = new Button { Content = sp, Padding = new Thickness(2), Background = null };
            btn.Click += (_, _) => _h.ShowPreview(new[] { path }, System.IO.Path.GetDirectoryName(path) ?? "");
            ToolTipService.SetToolTip(btn, project
                ? path + "\n工程视图：从工程恢复的预处理视图，透明底属于正常抠图，不是本次重新生成。"
                : cached
                    ? path + "\n缓存复用：使用本地已生成的 AI 图，不调用接口，也不消耗额度。"
                    : path + "\n本次新生成，已写入本地缓存。");
            _aiThumbs.Children.Add(btn);
        });
    }

    /// <summary>框选 + 去背景（有缓存），返回每张图预处理后的路径。</summary>
    private async Task<List<(Item It, string Path)>> Prepare(IProgress<string> progress, CancellationToken ct)
    {
        var res = new List<(Item, string)>();
        foreach (var it in _items)
        {
            ct.ThrowIfCancellationRequested();
            if (it.IsAi) { res.Add((it, it.Path)); continue; }   // AI 视图本身就是干净的品红底
            progress.Report($"处理 {System.IO.Path.GetFileName(it.Path)}：框选与去背景…");
            var p = await SubjectPrep.PrepareAsync(it.Path, it.Box, _h.S.Hy3d, progress, ct);
            res.Add((it, p));
        }
        return res;
    }

    private async Task ExtractKeys()
    {
        if (_items.Count == 0) { _h.Notify("请先添加图片", InfoBarSeverity.Warning); return; }
        await _h.RunAsync("提取主色", async (progress, ct) =>
        {
            var prepped = await Prepare(progress, ct);
            var colors = await Task.Run(() =>
            {
                var views = prepped.Select(p => { var img = RgbaImage.Load(p.Path); return (img, Silhouette.FromImage(img)); }).ToList();
                return ColorFlatten.ExtractKeyColors(views);
            }, ct);
            _keyColors.Clear();
            _keyColors.AddRange(colors);
            _onlyKeys = true;
            _onlyKeysCheck.IsChecked = true;   // 提取到主色就默认只用主色：勾选状态必须跟着变，否则界面与实际设置不一致
            RefreshKeys();
            _h.Notify($"提取到 {colors.Count} 种主色。可以点色块删掉不需要的，或在框选窗口里用吸色补充。", InfoBarSeverity.Success);
        });
    }

    private async Task ImportMeshAsync()
    {
        if (_running) return;
        var s = _h.S;
        if (s.Dirty && s.Model.CountSolid() > 0 && !await _h.ConfirmAsync("替换当前模型？", "导入的网格会替换当前未保存的模型。", "继续")) return;
        var pick = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(pick, _h.Hwnd);
        foreach (var e in new[] { ".obj", ".glb", ".stl", ".vox" }) pick.FileTypeFilter.Add(e);
        var f = await pick.PickSingleFileAsync();
        if (f == null) return;
        _log.Text = "";
        _running = true;
        try
        {
            await _h.RunAsync("导入三维模型", async (progress, ct) =>
            {
                IProgress<string> p = new Progress<string>(m => { Log(m); progress.Report(m); });
                var mesh = MeshLoader.Load(f.Path);
                bool hasTexture = mesh.Texture != null && mesh.VertexUVs?.Count == mesh.Vertices.Count;
                var views = new List<ViewImage>();
                var viewPaths = new Dictionary<ViewKind, string>();

                if (!hasTexture && _items.Count > 0)
                {
                    var prepped = await Prepare(p, ct);
                    foreach (var (it, path) in prepped.Where(x => x.It.Role < 6))
                    {
                        var kind = (ViewKind)it.Role;
                        if (viewPaths.ContainsKey(kind)) continue;
                        var v = ViewImage.Load(kind, path);
                        v.IsAi = it.IsAi;
                        views.Add(v);
                        viewPaths[kind] = path;
                    }
                }
                else if (_items.Count > 0)
                {
                    foreach (var it in _items.Where(x => x.Role < 6))
                    {
                        var kind = (ViewKind)it.Role;
                        if (!viewPaths.ContainsKey(kind)) viewPaths[kind] = it.Path;
                    }
                }

                var res = await Task.Run(() => MeshImporter.Import(mesh, s.Lib, new MeshImportOptions
                {
                    TargetHeight = _height,
                    BrandId = _brandIds[_brand],
                    MaxColors = _maxColors,
                    Name = System.IO.Path.GetFileNameWithoutExtension(f.Path),
                    Views = views,
                }, m => p.Report(m), ct), ct);

                s.Replace(res.Model, new ProjectMeta
                {
                    Title = System.IO.Path.GetFileNameWithoutExtension(f.Path),
                    Author = s.Settings.Author,
                    Watermark = s.Settings.Watermark,
                }, viewPaths);
                var how = res.UsedTexture ? "模型自带纹理上色" : res.UsedViews ? "参考视图投影上色" : "单色（无纹理/参考图）";
                Log($"导入完成：{res.Model.SX}×{res.Model.SY}×{res.Model.SZ}，{res.Model.CountSolid():N0} 颗，{res.Model.Palette.Count - 1} 色；{how}{(res.RotatedZUp ? "；Z-up 已转正" : "")}");
                _h.Notify($"模型已导入：{res.Model.CountSolid():N0} 颗 · {how}。可以开始编辑了。", InfoBarSeverity.Success);
                _h.GoStep(2);
            }, cancellable: true);
        }
        finally { _running = false; }
    }

    private async Task Run()
    {
        if (_items.Count == 0) { _h.Notify("请先添加图片", InfoBarSeverity.Warning); return; }
        var s = _h.S;
        var api = new Hunyuan3dApi(s.Settings);
        var localReady = Hy3dClient.IsInstalled(out var localWhy) && GpuProbe.CanRunLocalHunyuan;
        if (_method == 2 && !api.Configured)
        {
            _h.Notify("在线混元 3.1 未配置：请先到「设置 → 混元 3D 在线 API」填写 Key。", InfoBarSeverity.Warning);
            return;
        }
        if (_method == 1 && !localReady)
        {
            var why = !GpuProbe.CanRunLocalHunyuan ? "显存低于本地混元 2mv 的约 6 GB 门槛" : localWhy;
            _h.Notify("本地混元不可用：" + why + "。可改用在线 3.1 或轮廓雕刻。", InfoBarSeverity.Warning);
            return;
        }
        var useApi = _method == 2 || (_method == 0 && !localReady && api.Configured);
        if (s.Dirty && s.Model.CountSolid() > 0 && !await _h.ConfirmAsync("替换当前模型？", "生成的新模型会替换当前未保存的模型。", "继续")) return;
        _log.Text = "";
        _aiThumbs.Children.Clear();
        _running = true;
        try
        {
            await _h.RunAsync(useApi ? "在线生成三维模型" : "生成立体模型", async (progress, ct) =>
            {
                IProgress<string> p = new Progress<string>(m => { Log(m); progress.Report(m); });
                var prepped = await Prepare(p, ct);

                if (useApi)
                {
                    var front = prepped.FirstOrDefault(x => x.It.Role == 0);
                    if (front.It == null) front = prepped.FirstOrDefault();
                    if (front.It == null) throw new InvalidOperationException("缺少正面图。");
                    var apiDir = AppPaths.NewWorkDir("api3d");
                    var meshPath = await api.GenerateAsync(front.Path, apiDir, p, ct);
                    var mesh = MeshLoader.Load(meshPath);
                    var viewImages = new List<ViewImage>();
                    var viewPaths = new Dictionary<ViewKind, string>();
                    foreach (var (it, path) in prepped.Where(x => x.It.Role < 6))
                    {
                        var kind = (ViewKind)it.Role;
                        if (viewPaths.ContainsKey(kind)) continue;
                        var v = ViewImage.Load(kind, path);
                        v.IsAi = it.IsAi;
                        viewImages.Add(v);
                        viewPaths[kind] = path;
                    }
                    var import = await Task.Run(() => MeshImporter.Import(mesh, s.Lib, new MeshImportOptions
                    {
                        TargetHeight = _height,
                        BrandId = _brandIds[_brand],
                        MaxColors = _maxColors,
                        Name = _name.Text.Trim(),
                        Views = viewImages,
                    }, m => p.Report(m), ct), ct);
                    s.Replace(import.Model, new ProjectMeta
                    {
                        Title = _name.Text.Trim(),
                        Author = s.Settings.Author,
                        Watermark = s.Settings.Watermark,
                    }, viewPaths);
                    Log($"在线 3.1 完成：{import.Model.SX}×{import.Model.SY}×{import.Model.SZ}，{import.Model.CountSolid():N0} 颗，{import.Model.Palette.Count - 1} 色");
                    _h.Notify($"在线 3.1 模型已生成：{import.Model.CountSolid():N0} 颗 · {import.Model.Palette.Count - 1} 色。可以开始编辑了。", InfoBarSeverity.Success);
                    _h.GoStep(2);
                    return;
                }

                var inputs = prepped.Where(x => x.It.Role < 6).GroupBy(x => x.It.Role).Select(g => ((ViewKind)g.Key, g.First().Path)).ToList();
                var refs = prepped.Where(x => x.It.Role == 6).Select(x => x.Path).ToList();
                var method = _method == 1 ? ReconMethod.Hunyuan : _method == 3 ? ReconMethod.VisualHull : (localReady ? ReconMethod.Hunyuan : ReconMethod.VisualHull);
                var opt = new ReconOptions
                {
                    Method = method,
                    TargetHeight = _height,
                    AiCompleteViews = _complete, AiFlatColors = _flat, CartoonStyle = _cartoon, InferSymmetricSides = _sym,
                    ReferencePhotos = refs, SubjectHint = _hint.Text.Trim(), Name = _name.Text.Trim(),
                    Quantize = new QuantizeOptions { BrandId = _brandIds[_brand], MaxColors = _maxColors },
                    FlattenPhotos = _flatten,
                    KeyColors = _onlyKeys ? _keyColors.ToList() : new List<Rgb>(),
                    AiViews = prepped.Where(x => x.It.IsAi && x.It.Role < 6).Select(x => (ViewKind)x.It.Role).ToHashSet(),
                    OnAiImage = (label, path, cached) => AddThumb(label, path, cached),
                };
                var ai = new ImageAi(s.Settings) { ForceRegenerate = _force };
                var pipe = new ReconstructPipeline(s.Lib, s.Hy3d, ai);
                var res = await Task.Run(() => pipe.RunAsync(inputs, opt, p, ct), ct);
                var views = res.Views.Where(v => !v.ShapeOnly).GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.First().SourcePath);
                s.Replace(res.Model, new ProjectMeta { Title = opt.Name, Author = s.Settings.Author, Watermark = s.Settings.Watermark }, views);
                Log($"完成：{res.Model.SX}×{res.Model.SY}×{res.Model.SZ}，{res.Model.CountSolid():N0} 颗，{res.Model.Palette.Count - 1} 色");
                _h.Notify($"模型已生成：{res.Model.CountSolid():N0} 颗 · {res.Model.Palette.Count - 1} 色。可以开始编辑了。", InfoBarSeverity.Success);
                _h.GoStep(2);
            }, cancellable: true);
        }
        finally { _running = false; }
    }
}
