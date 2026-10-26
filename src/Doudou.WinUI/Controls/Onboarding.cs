using Doudou.Core.Services;
using Doudou.Editor;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using static Doudou.WinUI.UiKit;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Doudou.WinUI.Controls;

/// <summary>主界面导览的一站：说明文字 + 要打亮的真实控件。</summary>
public sealed record TourStop(string Title, string Body, Func<FrameworkElement?> Target, string Hint = "", bool NeedsWorkspace = true, Action? Before = null);

/// <summary>
/// 首次启动引导：第一步选择本地 AI 依赖，第二步填写生图 API，最后在主界面上用聚光灯逐项介绍真实按钮。
/// 依赖和 API 都可以跳过；导览支持方向键翻页、Esc 关闭，之后可从右上角「使用帮助」重看。
/// </summary>
public sealed class Onboarding : Grid
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly TourStop[] _stops;
    private readonly Action _enterWorkspace;
    private readonly Action? _restoreWelcome;

    // ── 设置卡 ──
    private readonly Grid _wizard = new();
    private readonly ContentControl _wizTag = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ContentControl _wizHeading = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _wizSub = new() { FontSize = 13.5, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text2), MaxWidth = 720, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ContentControl _wizBody = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _wizDots = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _wizBack, _wizNext;
    private readonly HyperlinkButton _wizSkip;
    private int _stage;

    // ── 本地依赖 ──
    private readonly StackPanel _depsPage = new() { Spacing = 12 };
    private readonly StackPanel _depOptions = new() { Spacing = 10 };
    private readonly Border _depReadyBox;
    private readonly TextBlock _depReadyText;
    private readonly RadioButton _rbFull, _rbCut, _rbNone;
    private readonly TextBlock _depNote = new() { FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3), TextWrapping = TextWrapping.Wrap };
    private readonly Border _depBanner;
    private readonly TextBlock _depBannerTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _depBannerDetail = new() { FontSize = Theme.F2, Foreground = Theme.B(Theme.Text2), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _depBannerBar = new() { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
    private readonly HyperlinkButton _depBannerCancel;
    private CancellationTokenSource? _depCts;
    private bool _depBusy;
    private bool _depsStarted;

    // ── 生图 API ──
    private readonly StackPanel _apiPage = new() { Spacing = 12 };
    private readonly TextBox _api = new() { PlaceholderText = "https://服务商地址（不用带 /v1）", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly PasswordBox _key = new() { PlaceholderText = "sk-…", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoSuggestBox _model = new() { PlaceholderText = "自动获取或手填", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _apiStatus = new() { FontSize = Theme.F2, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text2) };
    private readonly Button _fetch;
    private readonly List<string> _knownModels = new();
    private Task? _fetchTask;
    private bool _fetching;
    private bool _apiHydrated;
    private string _lastFetchSource = "";

    // ── 主界面聚光灯导览 ──
    private readonly Grid _coach = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Canvas _coachCanvas = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Rectangle _shadeTop = Shade(), _shadeBottom = Shade(), _shadeLeft = Shade(), _shadeRight = Shade();
    private readonly Border _ring = new() { BorderBrush = Theme.B(Theme.Orange), BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Rectangle _blocker = new() { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private Border _bubble = null!;
    private readonly TextBlock _bubbleStep = new() { FontFamily = Theme.Display, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Theme.B(Theme.Orange), CharacterSpacing = 100, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock _bubbleTitle = null!, _bubbleBody = null!, _bubbleHint = null!;
    private readonly StackPanel _bubbleDots = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
    private Button _bubblePrev = null!, _bubbleNext = null!;
    private FrameworkElement? _target;
    private int _tourIndex = -1;
    private bool _tourBusy;

    public Onboarding(IHost h, IReadOnlyList<TourStop> stops, Action enterWorkspace, Action? restoreWelcome = null)
    {
        _h = h;
        _stops = stops.ToArray();
        _enterWorkspace = enterWorkspace;
        _restoreWelcome = restoreWelcome;
        Visibility = Visibility.Collapsed;
        IsTabStop = true;

        _wizBack = Btn("上一步", () => GoStage(0), G.Back);
        _wizNext = Btn("继续", OnWizardNext, G.Play, accent: true);
        _wizNext.FontWeight = FontWeights.SemiBold;
        _wizBack.Margin = _wizNext.Margin = new Thickness(0);
        _wizSkip = new HyperlinkButton
        {
            Content = "跳过全部", Padding = new Thickness(0), MinWidth = 0, FontSize = Theme.F2,
            Foreground = Theme.B(Theme.Text3), VerticalAlignment = VerticalAlignment.Center,
        };
        _wizSkip.Click += (_, _) => DismissAll();

        _rbFull = DepOption("下载全套本地依赖", "混元3D 形体 + ISNet 抠图，约 10 GB。照片不出本机，不消耗生图额度，断网也能继续。", "推荐");
        _rbCut = DepOption("只下载抠图模型", "约 300 MB，纯 CPU 可用。配合生图 API 或 CPU 轮廓雕刻。", "");
        _rbNone = DepOption("暂不下载", "先用 CPU 轮廓雕刻也能完整走完流程；之后可在「设置 → 本地 AI 模型」补装。", "");
        _rbFull.GroupName = _rbCut.GroupName = _rbNone.GroupName = "onbDeps";
        _depOptions.Children.Add(_rbFull);
        _depOptions.Children.Add(_rbCut);
        _depOptions.Children.Add(_rbNone);
        _depReadyText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Green) };
        _depReadyBox = new Border
        {
            Background = Theme.B(Theme.Green, 0x14), BorderBrush = Theme.B(Theme.Green, 0x70), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children =
                        {
                            new FontIcon { Glyph = G.Check, FontSize = 16, Foreground = Theme.B(Theme.Green) },
                            new TextBlock { Text = "本地 AI 已就绪", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Text1) },
                        },
                    },
                    _depReadyText,
                },
            },
        };
        _depsPage.Children.Add(_depReadyBox);
        _depsPage.Children.Add(_depOptions);
        _depsPage.Children.Add(_depNote);
        _depsPage.Children.Add(new TextBlock
        {
            Text = "点「继续」后会立即在后台下载，不耽误后面的 API 设置。",
            FontSize = Theme.F2, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Blue),
        });

        _depBannerCancel = new HyperlinkButton
        {
            Content = "取消下载", Padding = new Thickness(0), MinWidth = 0, FontSize = Theme.F2,
            Foreground = Theme.B(Theme.Text3), Visibility = Visibility.Collapsed,
        };
        _depBannerCancel.Click += (_, _) => { _depCts?.Cancel(); DependencyInstaller.Cancel(); _depBannerCancel.IsEnabled = false; };
        _depBanner = BuildDepBanner();

        _api.LostFocus += (_, _) => _ = AutoFetchAsync();
        _key.LostFocus += (_, _) => _ = AutoFetchAsync();
        _model.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = _model.Text.Trim();
            _model.ItemsSource = _knownModels.Where(m => q.Length == 0 || m.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        _model.GotFocus += (_, _) =>
        {
            if (_knownModels.Count == 0) _ = EnsureFetchAsync(false);
            _model.ItemsSource = _knownModels;
            _model.IsSuggestionListOpen = _knownModels.Count > 0;
        };
        _model.SuggestionChosen += (_, e) => _model.Text = e.SelectedItem as string ?? _model.Text;
        _fetch = Btn("获取模型", () => _ = EnsureFetchAsync(true), G.Refresh, tip: "读取服务商的 /v1/models，从返回列表里选择生图模型");
        _fetch.Margin = new Thickness(0);
        BuildApiPage();

        BuildWizard();
        BuildCoach();
        Children.Add(_wizard);
        Children.Add(_coach);
        GoStage(0);
        KeyDown += OnKey;
        SizeChanged += (_, _) => { if (_coach.Visibility == Visibility.Visible) LayoutCoach(); };
    }

    public bool Done => S.Settings.OnboardingDone;

    /// <summary>首次启动显示完整三步；从「使用帮助」重看时只显示主界面导览。</summary>
    public void Show(bool firstRun = true)
    {
        Visibility = Visibility.Visible;
        Opacity = 0;
        Focus(FocusState.Programmatic);
        if (firstRun)
        {
            _wizard.Visibility = Visibility.Visible;
            _coach.Visibility = Visibility.Collapsed;
            GoStage(0);
        }
        else
        {
            StartTour();
        }
        var sb = new Storyboard();
        var a = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(200)) };
        Storyboard.SetTarget(a, this);
        Storyboard.SetTargetProperty(a, "Opacity");
        sb.Children.Add(a);
        sb.Begin();
    }

    // ═════════════════ 设置卡 ═════════════════

    private void BuildWizard()
    {
        _wizard.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xF4, 0x0A, 0x12, 0x20), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xFA, 0x05, 0x08, 0x0E), Offset = 1 },
            },
        };
        var card = new Border
        {
            MaxWidth = 980,
            Padding = new Thickness(34, 26, 34, 26),
            Background = Theme.B(Theme.Navy, 0xF7),
            CornerRadius = new CornerRadius(3, 24, 3, 24),
            BorderBrush = Theme.B(Theme.Orange, 0xB0),
            BorderThickness = new Thickness(1.5, 1.5, 1.5, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_wizTag);
        Grid.SetColumn(_wizSkip, 1);
        head.Children.Add(_wizSkip);
        sp.Children.Add(head);
        sp.Children.Add(new Border { Height = 1, Background = Theme.B(Theme.Orange, 0x50), Margin = new Thickness(0, 10, 0, 16) });
        sp.Children.Add(_wizHeading);
        sp.Children.Add(_wizSub);
        sp.Children.Add(new Border { Height = 18 });
        sp.Children.Add(_wizBody);
        sp.Children.Add(_depBanner);

        var foot = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(_wizDots);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nav.Children.Add(_wizBack);
        nav.Children.Add(_wizNext);
        Grid.SetColumn(nav, 1);
        foot.Children.Add(nav);
        sp.Children.Add(foot);

        card.Child = sp;
        _wizard.Children.Add(new ScrollViewer
        {
            Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        });
    }

    private RadioButton DepOption(string title, string sub, string badge)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Text1) });
        if (badge.Length > 0) head.Children.Add(Theme.Tag(badge, Theme.Orange, 10, Theme.Navy));
        var text = new TextBlock { Text = sub, FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text3) };
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(head);
        body.Children.Add(text);
        var card = new Border
        {
            Background = Theme.B(Theme.Surface2), BorderBrush = Theme.B(Theme.Text3, 0x35), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = body,
        };
        var rb = new RadioButton
        {
            Content = card, GroupName = "onbDeps", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Margin = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        return rb;
    }

    private Border BuildDepBanner()
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_depBannerTitle);
        Grid.SetColumn(_depBannerCancel, 1);
        head.Children.Add(_depBannerCancel);
        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(head);
        sp.Children.Add(_depBannerDetail);
        sp.Children.Add(_depBannerBar);
        return new Border
        {
            Child = sp, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0),
            Background = Theme.B(Theme.Blue, 0x14), BorderBrush = Theme.B(Theme.Blue, 0x70), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
        };
    }

    private void BuildApiPage()
    {
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
        var skip = new HyperlinkButton { Content = "暂不配置，直接继续", Padding = new Thickness(0), MinWidth = 0, FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3) };
        skip.Click += (_, _) => StartTour();

        _apiPage.Children.Add(Row("接口地址", _api));        _apiPage.Children.Add(Row("API Key", _key));
        _apiPage.Children.Add(Row("模型", modelRow));
        _apiPage.Children.Add(_apiStatus);
        _apiPage.Children.Add(Caption("可用模型：gpt-image、Gemini Nano Banana、Seedream、Qwen-Image 等；只要服务商支持 OpenAI 兼容的 /v1/images/edits 就能用。"));
        _apiPage.Children.Add(Caption("Key 保存在本机；登录账号后会加密同步。照片会发送到所填服务商。"));
        _apiPage.Children.Add(Flow(providerHint, affiliate, providerTail));
        _apiPage.Children.Add(skip);
        SetApiStatus("填写地址和 Key 后点「获取模型」，会自动列出可用模型。", null);
    }

    private void GoStage(int stage)
    {
        _stage = stage;
        _wizBody.Content = stage == 0 ? _depsPage : _apiPage;
        _wizTag.Content = Theme.Tag(stage == 0 ? "首次启动 · SETUP 1/2" : "连接服务 · SETUP 2/2", Theme.Orange, 12, Theme.Navy);
        _wizHeading.Content = stage == 0
            ? Theme.Heading("先把本地 AI 准备好", "LOCAL AI · OPTIONAL", 30)
            : Theme.Heading("接一个生图 API", "IMAGE API · OPTIONAL", 30);
        _wizSub.Text = stage == 0
            ? "本地依赖把重建和抠图留在本机：照片不上传、不消耗生图额度、断网也能继续。现在下载，或稍后在设置里补装。"
            : "只用来整理照片、补全视角和取色，不直接生成豆图。没有 API 也能用纯本地流程完成作品。";
        _wizBack.Visibility = stage == 0 ? Visibility.Collapsed : Visibility.Visible;
        _wizNext.Content = Label(stage == 0 ? "继续" : "保存并继续", G.Play);
        UpdateWizardDots();
        if (stage == 0) RefreshDeps();
        if (stage == 1) HydrateApi();
    }

    private void UpdateWizardDots()
    {
        _wizDots.Children.Clear();
        for (int i = 0; i < 2; i++)
            _wizDots.Children.Add(new Border
            {
                Width = i == _stage ? 22 : 8, Height = 4, CornerRadius = new CornerRadius(2),
                Background = Theme.B(i == _stage ? Theme.Orange : Theme.Text3, i == _stage ? (byte)255 : (byte)0x60),
            });
    }

    private void OnWizardNext()
    {
        if (_stage == 0)
        {
            BeginChosenInstall();
            GoStage(1);
            return;
        }
        _ = SaveApiAndTourAsync();
    }

    // ═════════════════ 本地依赖 ═════════════════

    private void RefreshDeps()
    {
        bool full = DependencyInstaller.Hy3dReady;
        bool cut = DependencyInstaller.CutoutReady;
        _depReadyBox.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        _depOptions.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        _depReadyText.Text = "混元3D 与 ISNet 抠图都已安装，可以离线重建，照片不会上传。";

        var free = FreeBytes();
        bool nvidia = HasNvidia();
        var note = new List<string>();
        note.Add($"完整依赖约 10 GB，抠图约 300 MB；当前可用 {(free > 0 ? DependencyInstaller.FormatSize(free) : "未知")}。");
        note.Add("下载会自动测速选择最快镜像；大文件多线程分段，可断点续传。");
        note.Add(nvidia ? "检测到 NVIDIA 显卡，可以使用混元3D。" : "未检测到 NVIDIA 显卡；混元3D 需要 NVIDIA 显卡，建议只装抠图。");
        if (cut && !full) note.Add("ISNet 抠图已就绪，可补装全套以获得本地混元3D。");
        _depNote.Text = string.Join(" ", note);

        bool canFull = free < 0 || free > 15_000_000_000;
        bool canCut = free < 0 || free > 1_500_000_000;
        _rbFull.IsEnabled = !_depBusy && !full && canFull;
        _rbCut.IsEnabled = !_depBusy && !full && !cut && canCut;
        _rbNone.IsEnabled = !_depBusy;

        if (_rbFull.IsChecked != true && _rbCut.IsChecked != true && _rbNone.IsChecked != true)
        {
            if (!full)
            {
                if (nvidia && canFull) _rbFull.IsChecked = true;
                else if (!cut && canCut) _rbCut.IsChecked = true;
                else _rbNone.IsChecked = true;
            }
        }
        if (full) _rbNone.IsChecked = true;
    }

    private void BeginChosenInstall()
    {
        if (_depsStarted || _depBusy || DependencyInstaller.Hy3dReady) return;
        bool cut = _rbCut.IsChecked == true;
        bool full = _rbFull.IsChecked == true;
        if (!cut && !full) return;
        if (DependencyInstaller.IsRunning)
        {
            SetDepBanner("已有依赖任务在进行", "可以在「设置 → 本地 AI 模型」查看进度；完成后本页会自动更新。", 0, false, false);
            return;
        }
        _depsStarted = true;
        _ = RunDepInstallAsync(cut);
    }

    private async Task RunDepInstallAsync(bool cutoutOnly)
    {
        _depBusy = true;
        _depCts = new CancellationTokenSource();
        _rbFull.IsEnabled = _rbCut.IsEnabled = _rbNone.IsEnabled = false;
        SetDepBanner("准备安装本地依赖", "正在读取依赖清单…", 0, true, false);
        var progress = new Progress<DepProgress>(p =>
        {
            SetDepBanner("正在安装本地 AI 依赖 · " + p.Stage, p.Detail, p.Percent, true, false);
        });
        bool ok = false;
        try
        {
            await DependencyInstaller.InstallAsync(cutoutOnly, progress, _depCts.Token);
            ok = true;
            SetDepBanner("本地 AI 依赖已就绪", cutoutOnly ? "ISNet 抠图已经可以使用。" : "混元3D 与 ISNet 抠图已经可以使用。", 100, false, true);
            _h.Notify("本地 AI 依赖安装完成", InfoBarSeverity.Success);
            _depReadyBox.Visibility = Visibility.Visible;
            _depReadyText.Text = cutoutOnly ? "ISNet 抠图已就绪；之后可在设置里补装混元3D。" : "混元3D 与 ISNet 抠图都已安装，可以离线重建，照片不会上传。";
            _depOptions.Visibility = Visibility.Collapsed;
            _rbNone.IsChecked = true;
            return;
        }
        catch (OperationCanceledException)
        {
            SetDepBanner("已取消下载", "已下载的部分会保留，可以稍后在设置里继续。", 0, false, false);
        }
        catch (Exception e)
        {
            SetDepBanner("安装失败", e.Message, 0, false, false);
            _h.Notify("依赖安装失败：" + e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _depBusy = false;
            _depCts.Dispose();
            _depCts = null;
            _depsStarted = ok;
            if (!ok)
            {
                _rbFull.IsEnabled = !DependencyInstaller.Hy3dReady;
                _rbCut.IsEnabled = !DependencyInstaller.CutoutReady;
                _rbNone.IsEnabled = true;
            }
        }
    }

    private void SetDepBanner(string title, string detail, double percent, bool running, bool success)
    {
        _depBanner.Visibility = Visibility.Visible;
        _depBannerTitle.Text = title;
        _depBannerDetail.Text = detail;
        _depBannerBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _depBannerBar.Value = Math.Clamp(percent, 0, 100);
        _depBannerCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _depBannerCancel.IsEnabled = true;
        _depBanner.BorderBrush = Theme.B(success ? Theme.Green : running ? Theme.Blue : Theme.Red, 0x80);
        _depBanner.Background = Theme.B(success ? Theme.Green : running ? Theme.Blue : Theme.Red, 0x14);
        _depBannerTitle.Foreground = Theme.B(success ? Theme.Green : Theme.Text1);
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

    private static bool HasNvidia() => File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

    // ═════════════════ 生图 API ═════════════════

    private void HydrateApi()
    {
        if (_apiHydrated) return;
        _apiHydrated = true;
        var s = S.Settings;
        _api.Text = ImageAi.NormalizeBase(s.ImageApiBase);
        _key.Password = s.ImageApiKey;
        _model.Text = s.ImageModel;
        if (_api.Text.Length > 0 && _key.Password.Length > 0 && _knownModels.Count == 0)
            _ = EnsureFetchAsync(false);
    }

    private void SetApiStatus(string text, bool? ok)
    {
        _apiStatus.Text = text;
        _apiStatus.Foreground = Theme.B(ok == true ? Theme.Green : ok == false ? Theme.Red : Theme.Text2);
    }

    private Task EnsureFetchAsync(bool manual)
    {
        if (_fetching) return _fetchTask ?? Task.CompletedTask;
        _fetchTask = FetchModelsAsync(manual);
        return _fetchTask;
    }

    private async Task AutoFetchAsync()
    {
        var baseUrl = ImageAi.NormalizeBase(_api.Text);
        if (baseUrl.Length == 0 || _key.Password.Trim().Length == 0) return;
        if (baseUrl + "|" + _key.Password.Trim() == _lastFetchSource) return;
        await EnsureFetchAsync(false);
    }

    private async Task FetchModelsAsync(bool manual)
    {
        if (_fetching) return;
        var baseUrl = ImageAi.NormalizeBase(_api.Text);
        var key = _key.Password.Trim();
        if (baseUrl.Length == 0) { if (manual) SetApiStatus("请先填写接口地址。", false); return; }
        if (key.Length == 0) { if (manual) SetApiStatus("请先填写 API Key。", false); return; }

        _fetching = true;
        _lastFetchSource = baseUrl + "|" + key;
        _fetch.IsEnabled = false;
        SetApiStatus("正在连接并读取 /v1/models …", null);
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
                SetApiStatus($"获取失败：{why}。可修正后重试。", false);
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
            catch (System.Text.Json.JsonException) { /* 非 JSON 会在下面提示 */ }

            if (ids.Count == 0)
            {
                SetApiStatus("接口已连通，但没有返回模型列表。可以直接填写模型名，或向服务商确认是否支持 /v1/models。", true);
                return;
            }

            static bool LooksImage(string id) =>
                new[] { "image", "imagen", "dall-e", "flux", "seedream", "seededit", "nano-banana", "qwen-image", "kolors", "recraft", "sdxl", "stable-diffusion" }
                    .Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase));
            var imageCount = ids.Count(LooksImage);
            _knownModels.Clear();
            _knownModels.AddRange(ids.OrderByDescending(LooksImage).ThenBy(x => x, StringComparer.OrdinalIgnoreCase));
            _model.ItemsSource = _knownModels;
            var picked = _model.Text.Trim();
            if (picked.Length == 0 && _knownModels.Count > 0)
            {
                picked = _knownModels[0];
                _model.Text = picked;
                SetApiStatus($"已连接，获取到 {ids.Count} 个模型" + (imageCount > 0 ? $"（{imageCount} 个图像模型）" : "") + $"。已自动选择 {picked}，点开下拉可更换。", true);
                return;
            }
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

    private async Task SaveApiAndTourAsync()
    {
        if (!await SaveApiAsync()) return;
        StartTour();
    }

    private async Task<bool> SaveApiAsync()
    {
        var s = S.Settings;
        var baseUrl = ImageAi.NormalizeBase(_api.Text);
        var key = _key.Password.Trim();
        var model = _model.Text.Trim();
        _api.Text = baseUrl;

        if (baseUrl.Length == 0 && key.Length == 0)
        {
            s.ImageApiBase = "";
            s.ImageApiKey = "";
            s.ImageModel = "";
            s.Save();
            return true;
        }
        if (baseUrl.Length == 0 || key.Length == 0)
        {
            SetApiStatus("地址和 Key 需要一起填写；不想使用就都留空。", false);
            return false;
        }
        if (model.Length == 0)
        {
            if (_fetching) await (_fetchTask ?? Task.CompletedTask);
            if (model.Length == 0 && _knownModels.Count == 0) await EnsureFetchAsync(true);
            model = _model.Text.Trim();
            if (model.Length == 0 && _knownModels.Count > 0)
            {
                model = _knownModels[0];
                _model.Text = model;
                SetApiStatus($"已自动选择 {model}，之后可在设置里更换。", true);
            }
            if (model.Length == 0)
            {
                SetApiStatus("请选择或填写生图模型后再继续；也可以点「暂不配置」。", false);
                return false;
            }
        }

        s.ImageApiBase = baseUrl;
        s.ImageApiKey = key;
        s.ImageModel = model;
        s.Save();
        SetApiStatus("已保存。", true);
        return true;
    }

    // ═════════════════ 主界面聚光灯导览 ═════════════════

    private void BuildCoach()
    {
        _coach.Children.Add(_coachCanvas);
        _coachCanvas.Children.Add(_shadeTop);
        _coachCanvas.Children.Add(_shadeBottom);
        _coachCanvas.Children.Add(_shadeLeft);
        _coachCanvas.Children.Add(_shadeRight);
        _coachCanvas.Children.Add(_blocker);
        _coachCanvas.Children.Add(_ring);

        _bubbleTitle = new TextBlock { FontFamily = Theme.Display, FontWeight = FontWeights.Bold, FontSize = 24, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text1), RenderTransform = new SkewTransform { AngleX = -8 } };
        _bubbleBody = new TextBlock { FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text2) };
        _bubbleHint = new TextBlock { FontSize = 11.5, LineHeight = 17, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Text3) };
        _bubblePrev = Btn("上一步", () => _ = ShowTourStopAsync(_tourIndex - 1), G.Back);
        _bubbleNext = Btn("下一步", () => _ = ShowTourStopAsync(_tourIndex + 1), G.Play, accent: true);
        _bubblePrev.Margin = _bubbleNext.Margin = new Thickness(0);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_bubbleStep);
        var skip = new HyperlinkButton { Content = "跳过说明", Padding = new Thickness(0), MinWidth = 0, FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3) };
        skip.Click += (_, _) => DismissAll();
        Grid.SetColumn(skip, 1);
        top.Children.Add(skip);

        var foot = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(_bubbleDots);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nav.Children.Add(_bubblePrev);
        nav.Children.Add(_bubbleNext);
        Grid.SetColumn(nav, 1);
        foot.Children.Add(nav);

        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(top);
        sp.Children.Add(_bubbleTitle);
        sp.Children.Add(_bubbleBody);
        sp.Children.Add(_bubbleHint);
        sp.Children.Add(foot);
        _bubble = new Border
        {
            Child = sp, MaxWidth = 420, Visibility = Visibility.Visible,
            Background = Theme.B(Theme.Navy, 0xFA), BorderBrush = Theme.B(Theme.Orange, 0xB0),
            BorderThickness = new Thickness(1.5, 1.5, 1.5, 4), CornerRadius = new CornerRadius(3, 18, 3, 18),
            Padding = new Thickness(20, 16, 20, 16),
        };
        _coachCanvas.Children.Add(_bubble);
    }

    private static Rectangle Shade() => new()
    {
        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC8, 0x04, 0x07, 0x0C)),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };

    private void StartTour()
    {
        _wizard.Visibility = Visibility.Collapsed;
        _coach.Visibility = Visibility.Visible;
        Focus(FocusState.Programmatic);
        _ = ShowTourStopAsync(0);
    }

    private async Task ShowTourStopAsync(int index)
    {
        if (_tourBusy) return;
        if (index < 0) return;
        if (index >= _stops.Length) { DismissAll(); return; }
        _tourBusy = true;
        try
        {
            // 首帧可能还没完成测量；等一次布局再判断控件是否可见。
            await Task.Delay(80);
            UpdateLayout();
            for (int i = index; i < _stops.Length; i++)
            {
                var stop = _stops[i];
                if (stop.NeedsWorkspace)
                {
                    _enterWorkspace();
                    await Task.Delay(80);
                    UpdateLayout();
                }
                stop.Before?.Invoke();
                await Task.Delay(80);
                UpdateLayout();
                var target = stop.Target();
                if (target == null || !IsReallyVisible(target)) continue;

                _tourIndex = i;
                _target = target;
                UpdateBubble(stop, i);
                _ring.Visibility = Visibility.Visible;
                _blocker.Visibility = Visibility.Visible;
                LayoutCoach();
                Focus(FocusState.Programmatic);
                return;
            }
            DismissAll();
        }
        finally
        {
            _tourBusy = false;
        }
    }

    private void UpdateBubble(TourStop stop, int index)
    {
        _bubbleStep.Text = $"使用说明 · {index + 1:00} / {_stops.Length:00}";
        _bubbleTitle.Text = stop.Title;
        _bubbleBody.Text = stop.Body;
        _bubbleHint.Text = stop.Hint;
        _bubbleHint.Visibility = string.IsNullOrWhiteSpace(stop.Hint) ? Visibility.Collapsed : Visibility.Visible;
        _bubblePrev.IsEnabled = index > 0;
        _bubbleNext.Content = Label(index == _stops.Length - 1 ? "完成" : "下一步", index == _stops.Length - 1 ? G.Check : G.Play);
        _bubbleDots.Children.Clear();
        for (int i = 0; i < _stops.Length; i++)
            _bubbleDots.Children.Add(new Border
            {
                Width = i == index ? 18 : 6, Height = 4, CornerRadius = new CornerRadius(2),
                Background = Theme.B(i == index ? Theme.Orange : Theme.Text3, i == index ? (byte)255 : (byte)0x60),
            });
    }

    private void LayoutCoach()
    {
        if (_coach.Visibility != Visibility.Visible || _target == null) return;
        double w = ActualWidth, h = ActualHeight;
        if (w < 100 || h < 100) return;

        Windows.Foundation.Rect r;
        try
        {
            r = _target.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
        }
        catch (Exception) { return; }

        double x1 = Math.Max(0, r.X - 10), y1 = Math.Max(0, r.Y - 10);
        double x2 = Math.Min(w, r.X + r.Width + 10), y2 = Math.Min(h, r.Y + r.Height + 10);
        if (x2 - x1 < 2 || y2 - y1 < 2) return;

        SetShade(_shadeTop, 0, 0, w, y1);
        SetShade(_shadeBottom, 0, y2, w, h - y2);
        SetShade(_shadeLeft, 0, y1, x1, y2 - y1);
        SetShade(_shadeRight, x2, y1, w - x2, y2 - y1);

        Canvas.SetLeft(_ring, x1); Canvas.SetTop(_ring, y1);
        _ring.Width = x2 - x1; _ring.Height = y2 - y1;
        Canvas.SetLeft(_blocker, x1); Canvas.SetTop(_blocker, y1);
        _blocker.Width = x2 - x1; _blocker.Height = y2 - y1;

        double bw = Math.Min(400, w - 32);
        _bubble.Width = bw;
        _bubble.Measure(new Windows.Foundation.Size(bw, double.PositiveInfinity));
        double bh = Math.Max(160, _bubble.DesiredSize.Height);
        const double gap = 18, margin = 16;
        double cx = (x1 + x2) / 2, cy = (y1 + y2) / 2;
        double left, top;
        if (h - y2 >= bh + gap + margin) { left = Clamp(cx - bw / 2, margin, w - bw - margin); top = y2 + gap; }
        else if (y1 >= bh + gap + margin) { left = Clamp(cx - bw / 2, margin, w - bw - margin); top = y1 - gap - bh; }
        else if (w - x2 >= bw + gap + margin) { left = x2 + gap; top = Clamp(cy - bh / 2, margin, h - bh - margin); }
        else if (x1 >= bw + gap + margin) { left = x1 - gap - bw; top = Clamp(cy - bh / 2, margin, h - bh - margin); }
        else
        {
            left = Clamp(cx - bw / 2, margin, w - bw - margin);
            top = Clamp(cy - bh / 2, margin, h - bh - margin);
        }
        Canvas.SetLeft(_bubble, left);
        Canvas.SetTop(_bubble, top);
    }

    private static void SetShade(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w); r.Height = Math.Max(0, h);
        r.Visibility = w > 0 && h > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double Clamp(double v, double min, double max) => max < min ? min : Math.Clamp(v, min, max);

    private static bool IsReallyVisible(FrameworkElement e)
    {
        try
        {
            if (!e.IsLoaded || e.ActualWidth < 2 || e.ActualHeight < 2) return false;
            DependencyObject? cur = e;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && fe.Visibility != Visibility.Visible) return false;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_coach.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case VirtualKey.Right: case VirtualKey.Down: _ = ShowTourStopAsync(_tourIndex + 1); break;
            case VirtualKey.Left: case VirtualKey.Up: if (_tourIndex > 0) _ = ShowTourStopAsync(_tourIndex - 1); break;
            case VirtualKey.Escape: DismissAll(); break;
            default: e.Handled = true; return;
        }
        e.Handled = true;
    }

    private void DismissAll()
    {
        bool tourWasOpen = _coach.Visibility == Visibility.Visible || _tourIndex >= 0;
        Visibility = Visibility.Collapsed;
        _coach.Visibility = Visibility.Collapsed;
        _wizard.Visibility = Visibility.Collapsed;
        _target = null;
        _tourIndex = -1;
        if (tourWasOpen) _restoreWelcome?.Invoke();
        if (S.Settings.OnboardingDone) return;
        S.Settings.OnboardingDone = true;
        try { S.Settings.Save(); } catch (Exception) { /* 写不进去也不该挡住使用 */ }
    }
}
