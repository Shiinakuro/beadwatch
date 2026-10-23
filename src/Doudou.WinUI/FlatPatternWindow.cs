using Doudou.Core.Drawing;
using Doudou.Core.Imaging;
using Doudou.Core.Model;
using Doudou.Core.Recon;
using Doudou.Core.Services;
using Doudou.Editor;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI;

/// <summary>平行入口：单图 → AI 卡通平涂 → 豆格像素化 → 平面拼豆图纸。</summary>
public sealed class FlatPatternWindow : Window
{
    private readonly Session _s;
    private readonly IntPtr _hwnd;
    private readonly List<string> _brandIds;
    private readonly List<string> _brandTitles;

    private readonly Image _sourcePreview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _sourceName = Caption("还没有选择图片");
    private readonly Image _resultPreview = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _resultScroll = new()
    {
        ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.25f, MaxZoomFactor = 3f,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Visibility = Visibility.Collapsed,
    };
    private readonly Grid _placeholder = new();
    private readonly Grid _busyOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressRing _ring = new() { IsActive = false, Width = 42, Height = 42 };
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(14, 0, 14, 0) };
    private readonly TextBlock _status = Caption("选择一张图片开始。");
    private readonly TextBlock _busyText = Caption("");
    private readonly TextBlock _aiState = Caption("");
    private readonly TextBlock _sizeInfo = Caption("最长边决定成品大小，短边按主体比例自动计算。");
    private readonly TextBox _title = new() { PlaceholderText = "图纸标题（默认使用文件名）" };
    private readonly TextBox _hint = new() { PlaceholderText = "可选：一句话描述主体，帮助 AI 抓住特征" };
    private readonly StackPanel _leftPanel = new() { Spacing = 0 };
    private readonly Button _runBtn;
    private readonly Button _exportBtn;
    private readonly Button _pickBtn;
    private readonly Button _openBtn;
    private ComboBox _styleCombo = null!;

    private readonly DispatcherQueueTimer _previewTimer;
    private string _sourcePath = "";
    private string _preppedPath = "";
    private string _aiPath = "";
    private string _exportFolder = "";
    private VoxelModel? _model;
    private CancellationTokenSource? _cts;
    private bool _running, _refreshing, _closed;
    private int _style, _longSide = 64, _brand, _maxColors = 16;
    private bool _removeBg = true, _letters = true, _force;

    public FlatPatternWindow(Session s)
    {
        _s = s;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _brandIds = s.Lib.Brands.Keys.OrderBy(k => k).ToList();
        _brandTitles = _brandIds.Select(id => s.Lib.Brands[id].Title).ToList();
        _brand = Math.Max(0, _brandIds.IndexOf(s.Settings.DefaultBrand));
        _style = new ImageAi(s.Settings).Configured ? 0 : 1;

        Title = "平面拼豆 · " + Theme.AppName;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1360, area.Width - 40), h = Math.Min(940, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var cap = AppWindow.TitleBar;
            cap.ButtonBackgroundColor = Colors.Transparent;
            cap.ButtonInactiveBackgroundColor = Colors.Transparent;
            cap.ButtonForegroundColor = Theme.Text1;
            cap.ButtonInactiveForegroundColor = Theme.Text3;
            cap.ButtonHoverBackgroundColor = Theme.Orange;
            cap.ButtonHoverForegroundColor = Theme.Navy;
            cap.ButtonPressedBackgroundColor = Theme.OrangeDeep;
            cap.ButtonPressedForegroundColor = Theme.Navy;
        }

        _runBtn = WideBtn("生成平面图纸", async () => await GenerateAsync(), G.Magic);
        _exportBtn = WideBtn("导出 PNG / PDF / 清单", async () => await ExportAsync(), G.Print, accent: false);
        _exportBtn.IsEnabled = false;
        _pickBtn = Btn("选择一张图片", async () => await PickSourceAsync(), G.Photo, accent: true);
        _openBtn = Btn("打开输出文件夹", () => OpenFolder(), G.Folder);
        _openBtn.IsEnabled = false;

        Content = BuildRoot(out var drag);
        SetTitleBar(drag);
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(350);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => { if (!_running && !_closed) _ = RefreshPreviewAsync(); };
        Closed += (_, _) => { _closed = true; _cts?.Cancel(); };
        _title.TextChanged += (_, _) => SchedulePreview();
        UpdateAiState();
    }

    private UIElement BuildRoot(out Grid drag)
    {
        var root = new Grid { Background = Theme.Background() };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var bar = new Grid { Padding = new Thickness(16, 0, 150, 0) };
        drag = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.png"))),
            Width = 20, Height = 20,
        });
        var names = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = "平面拼豆", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 17, RenderTransform = new SkewTransform { AngleX = -10 },
        });
        names.Children.Add(new TextBlock
        {
            Text = "FLAT PATTERN", FontFamily = Theme.Display, FontSize = 11, CharacterSpacing = 160,
            Foreground = Theme.B(Theme.Orange), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        });
        head.Children.Add(names);
        drag.Children.Add(head);
        bar.Children.Add(drag);
        root.Children.Add(bar);

        Grid.SetRow(_info, 1);
        root.Children.Add(_info);

        var content = new Grid { Padding = new Thickness(14, 8, 14, 14), ColumnSpacing = 14 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(402) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        content.Children.Add(BuildLeft());

        var right = new Border
        {
            Background = Theme.B(Theme.Navy, 0xB8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Theme.B(Theme.Blue, 0x38), Padding = new Thickness(12),
        };
        var rg = new Grid();
        var ph = new StackPanel
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var pi = Icon(G.Grid, 54);
        pi.Foreground = Theme.B(Theme.Text3);
        pi.HorizontalAlignment = HorizontalAlignment.Center;
        ph.Children.Add(pi);
        ph.Children.Add(new TextBlock
        {
            Text = "生成后在这里预览平面图纸", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center,
        });
        ph.Children.Add(Caption("上传一张图，选择风格和豆格宽度，再点「生成平面图纸」。"));
        _placeholder.Children.Add(ph);
        rg.Children.Add(_placeholder);

        _resultPreview.Stretch = Stretch.Uniform;
        _resultPreview.HorizontalAlignment = HorizontalAlignment.Center;
        _resultScroll.Content = _resultPreview;
        rg.Children.Add(_resultScroll);

        _busyOverlay.Background = Theme.B(Theme.Navy, 0xB0);
        var bs = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bs.Children.Add(_ring);
        bs.Children.Add(new TextBlock
        {
            Text = "AI 生图 × 像素化", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center,
        });
        bs.Children.Add(_busyText);
        _busyOverlay.Children.Add(bs);
        rg.Children.Add(_busyOverlay);
        right.Child = rg;
        Grid.SetColumn(right, 1);
        content.Children.Add(right);
        return root;
    }

    private UIElement BuildLeft()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroll.Content = _leftPanel;

        var srcCard = new StackPanel { Spacing = 8 };
        var srcFrame = new Border
        {
            Height = 180, Background = Theme.B(Theme.Navy, 0x99), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = Theme.B(Theme.Blue, 0x40), Child = _sourcePreview,
        };
        srcCard.Children.Add(srcFrame);
        srcCard.Children.Add(_sourceName);
        srcCard.Children.Add(Flow(_pickBtn));
        srcCard.Children.Add(Caption("建议主体清楚、背景不要太花。AI 生图只生成一张卡通平涂参考，最后仍按品牌色卡逐格量化。"));
        _leftPanel.Children.Add(Card("① 上传图片", "一张就够。会先自动识别主体并去掉背景。", srcCard));

        var aiCard = new StackPanel { Spacing = 6 };
        aiCard.Children.Add(_aiState);
        _styleCombo = Combo(new[] { "AI 卡通平涂（推荐）", "直接像素化（不调用 AI）" }, _style, i => { _style = i; UpdateAiState(); SchedulePreview(); });
        aiCard.Children.Add(Row("风格", _styleCombo));
        aiCard.Children.Add(Row("描述", _hint));
        aiCard.Children.Add(Check("重新生成 AI 图（忽略缓存）", _force, v => _force = v));
        _leftPanel.Children.Add(Card("② AI 生图", "配置了图像 API 时，先把照片重画成大色块、强轮廓的正面图案；同一张图同设置会复用缓存。", aiCard));

        var pxCard = new StackPanel { Spacing = 4 };
        pxCard.Children.Add(SliderRow("最长边", 24, 120, _longSide, v => { _longSide = (int)v; UpdateSizeInfo(); SchedulePreview(); }, 1, "0", " 颗").Row);
        pxCard.Children.Add(Row("色卡", Combo(_brandTitles, _brand, i => { _brand = i; SchedulePreview(); })));
        pxCard.Children.Add(SliderRow("最多颜色", 4, 32, _maxColors, v => { _maxColors = (int)v; SchedulePreview(); }, 1, "0", " 色").Row);
        pxCard.Children.Add(Check("去掉背景（推荐）", _removeBg, v => { _removeBg = v; SchedulePreview(); }));
        pxCard.Children.Add(Check("格内标色号字母", _letters, v => { _letters = v; SchedulePreview(); }));
        pxCard.Children.Add(_sizeInfo);
        _leftPanel.Children.Add(Card("③ 像素化设置", "长边决定成品尺寸；颜色越少越好拼。修改后会自动更新预览。", pxCard));

        var titleCard = new StackPanel { Spacing = 4 };
        titleCard.Children.Add(Row("标题", _title));
        titleCard.Children.Add(Caption("导出时会写入图纸标题与材料清单；作者、水印沿用主程序设置。"));
        _leftPanel.Children.Add(Card("④ 图纸信息", null, titleCard));

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition());
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.Children.Add(scroll);
        var foot = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(_runBtn);
        foot.Children.Add(_status);
        foot.Children.Add(_exportBtn);
        foot.Children.Add(Flow(_openBtn));
        Grid.SetRow(foot, 1);
        outer.Children.Add(foot);
        return outer;
    }

    private void UpdateAiState()
    {
        var ai = new ImageAi(_s.Settings);
        _aiState.Text = _style == 1
            ? "当前不调用 AI：直接把原图变成豆格图。适合已经是平涂/像素风的图片。"
            : ai.Configured
                ? $"已连接 · {_s.Settings.ImageModel}。只生成一张参考图，单次约 ¥0.15；同图同设置会复用缓存。"
                : ai.HasEndpoint
                    ? "已填写生图地址和 Key，但还没选模型：到主程序「设置 → AI 图像服务」获取模型列表或手动填写模型名。"
                    : "还没有配置生图 API：可以先用「直接像素化」，或到主程序「设置 → AI 图像服务」填地址、Key 并选择模型。";
    }

    private void UpdateSizeInfo()
    {
        _sizeInfo.Text = $"最长边 {_longSide} 颗，短边按主体比例自动计算；豆径 {_s.Settings.BeadPitchMm} mm。";
    }

    private async Task PickSourceAsync()
    {
        var pick = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(pick, _hwnd);
        foreach (var e in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) pick.FileTypeFilter.Add(e);
        var f = await pick.PickSingleFileAsync();
        if (f != null) await SetSourceAsync(f.Path);
    }

    private async Task SetSourceAsync(string path)
    {
        _sourcePath = path;
        _preppedPath = "";
        _aiPath = "";
        _model = null;
        _exportBtn.IsEnabled = false;
        _openBtn.IsEnabled = false;
        _resultScroll.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Visible;
        _sourceName.Text = System.IO.Path.GetFileName(path);
        _title.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        _status.Text = "已选择图片，点「生成平面图纸」开始。";
        try
        {
            var bmp = new BitmapImage { DecodePixelWidth = 900 };
            using var fs = File.OpenRead(path);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            _sourcePreview.Source = bmp;
        }
        catch (Exception e) { ShowInfo("打开图片失败：" + e.Message, InfoBarSeverity.Error); }
        UpdateSizeInfo();
    }

    private async Task GenerateAsync()
    {
        if (_running) return;
        if (_sourcePath.Length == 0) { ShowInfo("请先选择一张图片。", InfoBarSeverity.Warning); return; }
        var ai = new ImageAi(_s.Settings);
        if (_style == 0 && !ai.Configured)
        {
            ShowInfo("还没有配置图像 API。请到主程序「设置 → AI 图像服务」配置，或把风格切到「直接像素化」。", InfoBarSeverity.Warning);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        IProgress<string> progress = new Progress<string>(m => Ui(() => { _status.Text = m; _busyText.Text = m; }));
        try
        {
            var work = AppPaths.NewWorkDir("flat");
            progress.Report("准备图片、识别主体…");
            _preppedPath = await SubjectPrep.PrepareAsync(_sourcePath, null, _s.Hy3d, progress, ct);

            string pixelSource;
            if (_style == 0)
            {
                progress.Report("AI 正在重画卡通平涂图…");
                var aiClient = new ImageAi(_s.Settings) { ForceRegenerate = _force };
                var prompt = ImageAi.FlatPatternPrompt(_hint.Text.Trim());
                _aiPath = await aiClient.EditAsync(new[] { _preppedPath }, prompt,
                    System.IO.Path.Combine(work, "flat_ai.png"), ct);
                pixelSource = _aiPath;
                progress.Report(aiClient.LastFromCache ? "AI 图命中缓存，直接像素化…" : "AI 图已生成，开始像素化…");
            }
            else
            {
                _aiPath = "";
                pixelSource = _removeBg ? _preppedPath : _sourcePath;
            }

            var res = await BuildAsync(pixelSource, progress, ct);
            _model = res.Model;
            await ShowPreviewAsync(res.Model, ct);
            var size = $"{res.Model.SX}×{res.Model.SY} 颗 · 约 {res.Model.SX * _s.Settings.BeadPitchMm / 10:F1}×{res.Model.SY * _s.Settings.BeadPitchMm / 10:F1} cm";
            _sizeInfo.Text = size;
            _status.Text = $"{size} · {res.Model.CountSolid():N0} 颗 · {res.Model.Palette.Count - 1} 色";
            _exportBtn.IsEnabled = true;
            ShowInfo("平面图纸已生成，可以导出 PNG / PDF 和材料清单。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ShowInfo("已取消。", InfoBarSeverity.Warning); }
        catch (Exception e)
        {
            LogError(e);
            ShowInfo("生成失败：" + (string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message), InfoBarSeverity.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task<FlatPatternResult> BuildAsync(string source, IProgress<string> progress, CancellationToken ct)
    {
        var img = await Task.Run(() => RgbaImage.Load(source), ct);
        var opt = new FlatPatternOptions
        {
            LongSide = _longSide,
            BrandId = _brandIds[_brand],
            MaxColors = _maxColors,
            RemoveBackground = _removeBg,
            Name = string.IsNullOrWhiteSpace(_title.Text) ? "平面拼豆" : _title.Text.Trim(),
        };
        return await Task.Run(() => FlatPattern.Build(img, _s.Lib, opt, m => progress.Report(m), ct), ct);
    }

    private async Task ShowPreviewAsync(VoxelModel model, CancellationToken ct)
    {
        var renderOptions = RenderOptions(1500, _letters);
        var preview = await Task.Run(() =>
        {
            using var bmp = FlatPatternRenderer.Render(model, _s.Lib, renderOptions);
            var path = System.IO.Path.Combine(AppPaths.NewWorkDir("flat_preview"), "preview.png");
            using (var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            using (var f = File.Create(path)) data.SaveTo(f);
            return path;
        }, ct);
        ct.ThrowIfCancellationRequested();
        var bmpImg = new BitmapImage { DecodePixelWidth = 1600 };
        using (var fs = File.OpenRead(preview)) await bmpImg.SetSourceAsync(fs.AsRandomAccessStream());
        Ui(() =>
        {
            _resultPreview.Width = 840;
            _resultPreview.Height = bmpImg.PixelWidth > 0 ? 840.0 * bmpImg.PixelHeight / bmpImg.PixelWidth : double.NaN;
            _resultPreview.Source = bmpImg;
            _placeholder.Visibility = Visibility.Collapsed;
            _resultScroll.Visibility = Visibility.Visible;
            _resultScroll.ChangeView(0, 0, 1f, true);
        });
    }

    private async Task RefreshPreviewAsync()
    {
        if (_refreshing || _running || _model == null || _closed) return;
        var source = CurrentSource();
        if (source.Length == 0) return;
        _refreshing = true;
        try
        {
            _status.Text = "更新像素化预览…";
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var res = await BuildAsync(source, new Progress<string>(), cts.Token);
            _model = res.Model;
            await ShowPreviewAsync(res.Model, cts.Token);
            _sizeInfo.Text = $"{res.Model.SX}×{res.Model.SY} 颗 · 约 {res.Model.SX * _s.Settings.BeadPitchMm / 10:F1}×{res.Model.SY * _s.Settings.BeadPitchMm / 10:F1} cm";
            _status.Text = $"{res.Model.SX}×{res.Model.SY} 颗 · {res.Model.CountSolid():N0} 颗 · {res.Model.Palette.Count - 1} 色";
            _exportBtn.IsEnabled = true;
        }
        catch (Exception e) { _status.Text = "预览更新失败：" + e.Message; }
        finally { _refreshing = false; }
    }

    private string CurrentSource()
    {
        if (_style == 0 && _aiPath.Length > 0 && File.Exists(_aiPath)) return _aiPath;
        if (_removeBg && _preppedPath.Length > 0 && File.Exists(_preppedPath)) return _preppedPath;
        return _sourcePath;
    }

    private void SchedulePreview()
    {
        if (_model == null || _running || _closed) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private FlatPatternRenderOptions RenderOptions(int maxWidth, bool letters) => new()
    {
        MaxWidth = maxWidth,
        Letters = letters,
        BrandId = _brandIds[_brand],
        Title = string.IsNullOrWhiteSpace(_title.Text) ? "平面拼豆" : _title.Text.Trim(),
        Author = _s.Settings.Author,
        Watermark = _s.Settings.Watermark,
        Notes = "禁止二传、二改、商用",
        PitchMm = _s.Settings.BeadPitchMm,
    };

    private async Task ExportAsync()
    {
        if (_model == null) { ShowInfo("请先生成平面图纸。", InfoBarSeverity.Warning); return; }
        var pick = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(pick, _hwnd);
        pick.FileTypeFilter.Add("*");
        var folder = await pick.PickSingleFolderAsync();
        if (folder == null) return;
        var title = System.IO.Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(_title.Text) ? "平面拼豆" : _title.Text.Trim());
        var safe = string.Concat(title.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var outDir = System.IO.Path.Combine(folder.Path, "平面拼豆_" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        SetBusy(true);
        _status.Text = "导出图纸…";
        try
        {
            var model = _model;
            var opts = RenderOptions(4200, _letters);
            var outp = await Task.Run(() => FlatPatternRenderer.Export(outDir, model, _s.Lib, opts,
                new Progress<string>(m => Ui(() => _status.Text = m))));
            _exportFolder = outp.Folder;
            _openBtn.IsEnabled = true;
            _status.Text = $"已导出 {outp.Files.Count} 个文件到：{outp.Folder}";
            ShowInfo("平面图纸已导出。", InfoBarSeverity.Success);
        }
        catch (Exception e) { ShowInfo("导出失败：" + e.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void OpenFolder()
    {
        if (_exportFolder.Length == 0) return;
        try { System.Diagnostics.Process.Start("explorer.exe", _exportFolder); }
        catch (Exception e) { ShowInfo("打开文件夹失败：" + e.Message, InfoBarSeverity.Error); }
    }

    private void SetBusy(bool busy)
    {
        if (_closed) return;
        _running = busy;
        _leftPanel.IsHitTestVisible = !busy;
        _leftPanel.Opacity = busy ? 0.55 : 1;
        _runBtn.IsEnabled = !busy;
        _pickBtn.IsEnabled = !busy;
        _exportBtn.IsEnabled = !busy && _model != null;
        _openBtn.IsEnabled = !busy && _exportFolder.Length > 0;
        _ring.IsActive = busy;
        _busyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) _busyText.Text = "";
    }

    private void Ui(Action a)
    {
        if (!_closed) DispatcherQueue.TryEnqueue(() => { if (!_closed) a(); });
    }

    private static void LogError(Exception e)
    {
        try
        {
            File.AppendAllText(System.IO.Path.Combine(AppPaths.UserData, "flat_error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\n\n");
        }
        catch (Exception) { }
    }

    private void ShowInfo(string msg, InfoBarSeverity sev)
    {
        Ui(() =>
        {
            _info.Message = msg;
            _info.Severity = sev;
            _info.IsOpen = true;
        });
    }
}
