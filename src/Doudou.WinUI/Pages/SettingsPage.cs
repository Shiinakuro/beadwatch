using Doudou.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>设置：AI 服务、默认值、本地模型状态、关于。</summary>
public sealed class SettingsPage : IStepPage
{
    public string Header => "设置";
    public string Description => "AI 服务、本地模型与默认值";
    public UIElement View { get; }

    private readonly IHost _h;
    private readonly TextBox _api = new(), _author = new(), _wm = new();
    private readonly PasswordBox _key = new();
    private readonly TextBox _hyBase = new();
    private readonly PasswordBox _hyKey = new();
    private readonly TextBlock _hyStatus = Caption("");
    private readonly TextBlock _gpuNote = Caption("");
    // AutoSuggestBox：输入即生效（可编辑 ComboBox 只有按回车/失焦才提交，点保存时会丢掉刚输入的值）
    private readonly AutoSuggestBox _model = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "自动获取或手填" };
    private List<string> _knownModels = new();
    private readonly TextBlock _apiStatus = Caption("");
    private readonly Button _fetch;
    private bool _fetching;
    private string _lastFetchSource = "";
    private readonly ComboBox _brand = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _pitch = new() { Minimum = 1, Maximum = 10, SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly TextBlock _stPython = Muted("");
    private readonly TextBlock _stHy = Muted("");
    private readonly TextBlock _stCut = Muted("");
    private readonly TextBlock _depNote = Caption("");
    private readonly TextBlock _depPct = Muted("");
    private readonly TextBlock _depDetail = Caption("");
    private readonly ProgressBar _depBar = new() { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
    private readonly Button _depAll, _depCut, _depCancel;
    private readonly List<string> _brandIds;
    private CancellationTokenSource? _depCts;
    private bool _depBusy;

    public SettingsPage(IHost h)
    {
        _h = h;
        var s = h.S.Settings;
        _brandIds = h.S.Lib.Brands.Keys.OrderBy(k => k).ToList();
        _brand.ItemsSource = _brandIds.Select(id => h.S.Lib.Brands[id].Title).ToList();
        _model.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = _model.Text.Trim();
            _model.ItemsSource = _knownModels.Where(m => q.Length == 0 || m.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        _model.GotFocus += (_, _) =>
        {
            if (_knownModels.Count == 0) _ = FetchModelsAsync(false);
            _model.ItemsSource = _knownModels;
            _model.IsSuggestionListOpen = true;
        };
        _model.SuggestionChosen += (_, e) => _model.Text = e.SelectedItem as string ?? _model.Text;
        _model.LostFocus += (_, _) => SaveModelOnly();

        _api.PlaceholderText = "https://服务商地址（不用带 /v1）";
        _hyBase.PlaceholderText = Hunyuan3dApi.DefaultBase;
        _api.LostFocus += async (_, _) => await AutoFetchModelsAsync();
        _key.LostFocus += async (_, _) => await AutoFetchModelsAsync();

        _fetch = Btn("获取模型", async () => await FetchModelsAsync(true), G.Refresh,
            tip: "读取服务商的 /v1/models，从返回列表里选择生图模型");
        _fetch.Margin = new Thickness(0);
        var modelRow = new Grid { ColumnSpacing = 8 };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition());
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.Children.Add(_model);
        Grid.SetColumn(_fetch, 1);
        modelRow.Children.Add(_fetch);

        var providerHint = new TextBlock { Text = "没有服务商？可用", FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3), VerticalAlignment = VerticalAlignment.Center };
        var affiliate = new HyperlinkButton
        {
            Content = "4Router（推广）", NavigateUri = new Uri("https://4router.net/register?aff=Rwm6"),
            Padding = new Thickness(0), MinWidth = 0, Margin = new Thickness(4, 0, 0, 0), FontSize = Theme.F2, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(affiliate, "推广链接：通过它注册不会增加你的费用，可以支持本项目继续更新。");
        var providerTail = new TextBlock { Text = "：OpenAI 兼容聚合，按量计费。", FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3), VerticalAlignment = VerticalAlignment.Center };

        _depAll = Btn("一键安装 / 修复", () => _ = StartInstallAsync(false), G.Download, accent: true,
            tip: "下载 Python、PyTorch CUDA、混元3D 权重与抠图模型，约 10 GB");
        _depCut = Btn("仅装抠图", () => _ = StartInstallAsync(true), G.Pick,
            tip: "只安装 ISNet 抠图（约 300 MB），不装显卡运行时");
        _depCancel = Btn("取消", () => { _depCts?.Cancel(); _depCancel.IsEnabled = false; _depDetail.Text = "正在取消，已下载的部分会保留…"; }, G.Delete);
        _depCancel.Visibility = Visibility.Collapsed;
        var depActions = new StackPanel { Orientation = Orientation.Horizontal };
        depActions.Children.Add(_depAll);
        depActions.Children.Add(_depCut);
        depActions.Children.Add(Btn("打开目录", () => System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root), G.Folder));
        depActions.Children.Add(_depCancel);

        var root = new StackPanel();
        root.Children.Add(Card("AI 图像服务",
            "可选。只处理照片：整理成正交视图、补全缺失角度、生成平涂取色参考；最终仍由三维重建和色卡量化出图，生图 API 不直接画豆图。",
            Row("接口地址", _api), Row("API Key", _key), Row("模型", modelRow),
            _apiStatus,
            Caption("可用模型：gpt-image、Gemini Nano Banana、Seedream、Qwen-Image 等；只要服务商支持 OpenAI 兼容的 /v1/images/edits 就能用。"),
            Caption("Key 只保存在本机。启用 API 后照片会发送到你填写的服务商；不想上传照片就别配置，纯本地也能完成重建与取色。"),
            Flow(providerHint, affiliate, providerTail)));
        root.Children.Add(Card("混元 3D 在线 API",
            "可选。用腾讯混元 3.1 在线生成网格，适合显存不足或想要更高形体质量；会消耗腾讯云积分，照片会上传到腾讯云。",
            Row("接口地址", _hyBase), Row("API Key", _hyKey), _hyStatus,
            Flow(Btn("测试 Key", async () => await TestHunyuan3dApiAsync(), G.Refresh),
                 Caption("单图约 20 积分/次；Key 只保存在本机。建议用正面图。")),
            Caption("默认 endpoint：https://api.ai3d.cloud.tencent.com/v1/ai3d/submit；模型 3.1。")));
        root.Children.Add(Card("默认", null, Row("色卡", _brand), Row("作者/拆图", _author), Row("图纸水印", _wm), Row("豆径 mm", _pitch)));
        root.Children.Add(Card("本地 AI 模型",
            "全套依赖把重建和抠图留在本机：照片不上传、不消耗生图额度，断网也能继续。",
            _gpuNote,
            Row("运行时", _stPython), Row("混元3D", _stHy), Row("抠图", _stCut),
            Caption("· 混元3D：从照片直接估出立体形体，凹槽、厚度与背面比纯轮廓更准。"),
            Caption("· ISNet：发丝、透明边缘和杂乱背景抠得更干净，省去手动框选。"),
            Caption("· 只装抠图（约 300 MB）就够用：可配合生图 API 或纯 CPU 轮廓雕刻。"),
            Caption("· 下载会自动测速选择最快镜像；大文件多线程分段，支持断点续传。"),
            _depNote, depActions, _depBar, _depPct, _depDetail));
        root.Children.Add(WideBtn("保存设置", Save, G.Save));
        root.Children.Add(Card("关于", null,
            Theme.Heading(Theme.AppName, Theme.AppNameEn + " · v" + typeof(SettingsPage).Assembly.GetName().Version?.ToString(3), 22),
            Caption(Theme.Slogan + "。\n三维重建 · 编辑 · 拆件 · 检查 · 图纸导出"),
            Caption("数据与设置保存在本机 userdata 目录。")));
        View = root;
    }

    public void OnEnter()
    {
        var s = _h.S.Settings;
        _api.Text = ImageAi.NormalizeBase(s.ImageApiBase); _key.Password = s.ImageApiKey; _model.Text = s.ImageModel;
        _hyBase.Text = string.IsNullOrWhiteSpace(s.Hunyuan3dApiBase) ? Hunyuan3dApi.DefaultBase : s.Hunyuan3dApiBase;
        _hyKey.Password = s.Hunyuan3dApiKey;
        _hyStatus.Text = s.Hunyuan3dApiKey.Trim().Length > 0 ? "已配置在线 API。显存不足或本地模型未安装时会自动使用它。" : "未配置：在线 3.1 需要填写 API Key。";
        _hyStatus.Foreground = Theme.B(Theme.Text2);
        _gpuNote.Text = GpuProbe.LocalChoiceText;
        _brand.SelectedIndex = Math.Max(0, _brandIds.IndexOf(s.DefaultBrand));
        _author.Text = s.Author; _wm.Text = s.Watermark; _pitch.Value = s.BeadPitchMm;
        if (ImageAi.NormalizeBase(s.ImageApiBase).Length > 0 && s.ImageApiKey.Length > 0 && _knownModels.Count == 0)
            _ = FetchModelsAsync(false);
        RefreshDeps();
    }

    private void RefreshDeps()
    {
        var py = DependencyInstaller.PythonReady;
        var cut = Hy3dClient.CutoutInstalled;
        var hy = Hy3dClient.IsInstalled(out _);
        void Mark(TextBlock t, bool ok, string yes, string no)
        {
            t.Text = ok ? yes : no;
            t.Foreground = Theme.B(ok ? Theme.Green : Theme.Text3);
        }
        Mark(_stPython, py, "已安装", "未安装");
        Mark(_stHy, hy, "已就绪", "未安装");
        Mark(_stCut, cut, "已就绪", "未安装");
        var free = FreeBytes();
        var gpu = HasNvidia() ? "检测到 NVIDIA 驱动" : "未检测到 NVIDIA 显卡（混元3D 需要 NVIDIA 显卡，可只安装抠图）";
        _depNote.Text = $"完整依赖约 10 GB，抠图约 300 MB，安装需预留约 15 GB。当前可用 {(free > 0 ? DependencyInstaller.FormatSize(free) : "未知")}；{gpu}。RTX 50 系会自动使用 CUDA 12.8 版 PyTorch。";
        _depAll.IsEnabled = !_depBusy;
        _depCut.IsEnabled = !_depBusy && !(py && cut);
        if (!_depBusy && hy && cut)
            _depDetail.Text = "本地混元3D 与抠图均已就绪。如果混元3D 无法调用显卡（例如 RTX 50 系），点击「一键安装 / 修复」会检查并升级 PyTorch，不会重下模型。";
        else if (!_depBusy && cut)
            _depDetail.Text = "ISNet 抠图已就绪；补装全部依赖后可用混元3D 本地生成形体。";
        else if (!_depBusy && py)
            _depDetail.Text = "Python 运行时已安装，但依赖不完整。点击「一键安装全部」会只补齐缺少的部分。";
    }

    private async Task StartInstallAsync(bool cutoutOnly)
    {
        if (_depBusy) return;
        if (!cutoutOnly && !HasNvidia())
        {
            var go = await _h.ConfirmAsync("未检测到 NVIDIA 显卡",
                "完整依赖会安装 CUDA 版 PyTorch，需要 NVIDIA 显卡（显存 ≥ 6 GB）。你的电脑仍可安装，但混元3D 无法运行。\n\n建议只安装「抠图」：约 300 MB，纯 CPU 可用，框选去背景效果更好。",
                "仍要安装全部", "取消");
            if (!go) return;
        }
        var size = cutoutOnly
            ? "预计下载约 300 MB"
            : "完整约 10 GB；已安装的 Python 与模型会跳过，RTX 50 系升级 PyTorch 时约 3.3 GB";
        var free = FreeBytes();
        var confirm = await _h.ConfirmAsync("安装 / 修复本地 AI 依赖",
            $"将安装或修复本地 AI 依赖到 {AppPaths.Root}。\n\n{size}。\n\n安装过程中请保持联网；已下载的部分会自动保留，取消或断网后可继续。\n当前可用空间：{(free > 0 ? DependencyInstaller.FormatSize(free) : "未知")}。",
            "开始", "取消");
        if (!confirm) return;

        _depBusy = true;
        _depCts = new CancellationTokenSource();
        _depAll.IsEnabled = _depCut.IsEnabled = false;
        _depCancel.Visibility = Visibility.Visible;
        _depCancel.IsEnabled = true;
        _depBar.Visibility = Visibility.Visible;
        _depBar.Value = 0;
        _depPct.Text = "0% · 准备";
        _depDetail.Text = "";
        var progress = new Progress<DepProgress>(x =>
        {
            _depBar.Value = x.Percent;
            _depPct.Text = $"{x.Percent:0}% · {x.Stage}";
            _depDetail.Text = x.Detail;
        });
        try
        {
            await DependencyInstaller.InstallAsync(cutoutOnly, progress, _depCts.Token);
            _h.Notify("本地 AI 依赖安装完成", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            _h.Notify("已取消安装；已下载的部分会保留，下次可继续。", InfoBarSeverity.Warning);
        }
        catch (Exception e)
        {
            _h.Notify("安装失败：" + e.Message, InfoBarSeverity.Error);
            _depDetail.Text = e.Message;
        }
        finally
        {
            _depBusy = false;
            _depCts.Dispose();
            _depCts = null;
            _depCancel.Visibility = Visibility.Collapsed;
            _depBar.Visibility = Visibility.Collapsed;
            _depPct.Text = "";
            RefreshDeps();
        }
    }

    private static long FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.Root);
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) { return -1; }
    }

    private static bool HasNvidia() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

    /// <summary>模型名离开输入框就保存，免得用户忘点「保存设置」。</summary>
    private void SaveModelOnly()
    {
        var t = _model.Text.Trim();
        if (t == _h.S.Settings.ImageModel) return;
        _h.S.Settings.ImageModel = t;
        _h.S.Settings.Save();
    }

    private void Save()
    {
        var s = _h.S.Settings;
        s.ImageApiBase = ImageAi.NormalizeBase(_api.Text);
        _api.Text = s.ImageApiBase;
        s.ImageApiKey = _key.Password.Trim();
        s.ImageModel = _model.Text.Trim();
        s.Hunyuan3dApiBase = string.IsNullOrWhiteSpace(_hyBase.Text) ? Hunyuan3dApi.DefaultBase : _hyBase.Text.Trim();
        _hyBase.Text = s.Hunyuan3dApiBase;
        s.Hunyuan3dApiKey = _hyKey.Password.Trim();
        if (_brand.SelectedIndex >= 0) s.DefaultBrand = _brandIds[_brand.SelectedIndex];
        s.Author = _author.Text.Trim();
        s.Watermark = _wm.Text.Trim();
        if (!double.IsNaN(_pitch.Value)) s.BeadPitchMm = _pitch.Value;
        s.Save();
        _h.Notify("设置已保存", InfoBarSeverity.Success);
    }

    private async Task TestHunyuan3dApiAsync()
    {
        _hyStatus.Text = "正在测试 Key…";
        _hyStatus.Foreground = Theme.B(Theme.Text2);
        var (ok, msg) = await Hunyuan3dApi.TestAsync(_hyBase.Text, _hyKey.Password);
        _hyStatus.Text = msg;
        _hyStatus.Foreground = Theme.B(ok ? Theme.Green : Theme.Red);
    }

    private void SetApiStatus(string text, bool? ok = null)
    {
        _apiStatus.Text = text;
        _apiStatus.Foreground = Theme.B(ok == true ? Theme.Green : ok == false ? Theme.Red : Theme.Text2);
    }

    private async Task AutoFetchModelsAsync()
    {
        var baseUrl = ImageAi.NormalizeBase(_api.Text);
        if (_fetching || baseUrl.Length == 0 || _key.Password.Trim().Length == 0) return;
        if (baseUrl + "|" + _key.Password.Trim() == _lastFetchSource) return;
        await FetchModelsAsync(false);
    }

    /// <summary>
    /// 读取 OpenAI 兼容的 /v1/models。成功时把模型名按「像图像模型」优先排序，方便直接挑；
    /// 服务不返回列表时仍允许手填模型名。
    /// </summary>
    private async Task FetchModelsAsync(bool manual)
    {
        var baseUrl = ImageAi.NormalizeBase(_api.Text);
        var key = _key.Password.Trim();
        if (baseUrl.Length == 0) { if (manual) SetApiStatus("请先填写接口地址。", false); return; }
        if (key.Length == 0) { if (manual) SetApiStatus("请先填写 API Key。", false); return; }
        if (_fetching) { if (manual) SetApiStatus("正在获取模型，请稍候…"); return; }

        _fetching = true;
        _lastFetchSource = baseUrl + "|" + key;
        _fetch.IsEnabled = false;
        SetApiStatus("正在连接并读取 /v1/models …");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, ImageAi.ModelsUrl(baseUrl));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                var why = (int)resp.StatusCode switch
                {
                    401 => "Key 无效或没有权限",
                    403 => "Key 无权访问该接口",
                    404 => "地址下没有 /v1/models，请确认填的是服务根地址",
                    _ => "服务返回 HTTP " + (int)resp.StatusCode,
                };
                SetApiStatus($"获取失败：{why}。可修正后点「获取模型」重试。", false);
                return;
            }

            var ids = new List<string>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
                    ids = data.EnumerateArray()
                        .Select(m => m.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() ?? "" : "")
                        .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (System.Text.Json.JsonException) { /* 非 JSON 也会在下面提示 */ }

            if (ids.Count == 0)
            {
                SetApiStatus("接口已连通，但没有返回模型列表。可以直接填写模型名，或向服务商确认是否支持 /v1/models。", true);
                return;
            }

            static bool LooksImage(string id) =>
                new[] { "image", "imagen", "dall-e", "flux", "seedream", "seededit", "nano-banana", "qwen-image", "kolors", "recraft", "sdxl", "stable-diffusion" }
                    .Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase));
            var imageCount = ids.Count(LooksImage);
            _knownModels = ids.OrderByDescending(LooksImage).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            _model.ItemsSource = _knownModels;
            var picked = _model.Text.Trim();
            var note = picked.Length == 0
                ? "点开「模型」下拉选择要用的模型。"
                : _knownModels.Contains(picked, StringComparer.OrdinalIgnoreCase) ? $"当前填写的 {picked} 在列表中。" : $"当前填写的 {picked} 不在列表中，仍可手动使用。";
            SetApiStatus($"已连接，获取到 {ids.Count} 个模型" + (imageCount > 0 ? $"（{imageCount} 个图像模型）" : "") + "。" + note, true);
        }
        catch (Exception e)
        {
            SetApiStatus("无法连接：" + e.Message + "。请检查地址、网络或代理设置。", false);
        }
        finally
        {
            _fetching = false;
            _fetch.IsEnabled = true;
        }
    }
}
