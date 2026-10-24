using Doudou.Core.Edit;
using Doudou.Core.IO;
using Doudou.Core.Recon;
using Doudou.Core.Services;
using Doudou.Editor;
using Doudou.WinUI.Controls;
using Doudou.WinUI.Pages;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI;

public interface IHost
{
    Session S { get; }
    VoxelView View { get; }
    XamlRoot XamlRoot { get; }
    IntPtr Hwnd { get; }
    Task RunAsync(string title, Func<IProgress<string>, CancellationToken, Task> work, bool cancellable = false);
    void Notify(string msg, InfoBarSeverity sev = InfoBarSeverity.Informational);
    void ShowPreview(IReadOnlyList<string> images, string folder);
    void GoStep(int step);
    Task<string?> PromptAsync(string title, string label, string def);
    Task<bool> ConfirmAsync(string title, string text, string ok = "确定", string cancel = "取消");
    Task OpenProjectAsync(string path);
    void OpenFlatPattern();
    AccountManager Accounts { get; }
    void OpenAccount();
}

/// <summary>主窗口：自绘标题栏 + 左侧步骤导航 + 中央三维视口（浮动工具栏）+ 右侧步骤面板。</summary>
public sealed class MainWindow : Window, IHost
{
    public Session S { get; }
    public AccountManager Accounts { get; }
    public VoxelView View { get; } = new();
    public XamlRoot XamlRoot => _root.XamlRoot;
    public IntPtr Hwnd { get; }

    private readonly Grid _root = new();
    private readonly NavigationView _nav = new();
    private readonly ContentControl _panelHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _panelTitle = new() { FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 26, RenderTransform = new SkewTransform { AngleX = -10 } };
    private readonly TextBlock _panelLine = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Orange), Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _panelDesc = Caption("");
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _projectName = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush"), Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBlock _stats = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hover = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush"), Margin = new Thickness(16, 0, 0, 0) };
    private readonly Button _undo, _redo;
    private readonly Grid _busy = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _busyTitle = new() { Style = Style("BodyStrongTextBlockStyle"), FontSize = 16 };
    private readonly TextBlock _busyText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush"), MaxWidth = 420 };
    private readonly Button _busyCancel = new() { Content = "取消", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _welcome = new();
    private readonly Grid _preview = new() { Visibility = Visibility.Collapsed };
    private FlatPatternWindow? _flatWindow;
    private AccountWindow? _accountWindow;
    private readonly ComboBox _previewPick = new() { MinWidth = 260 };
    private readonly ScrollViewer _previewScroll = new() { ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 4f, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled };
    private readonly Image _previewImage = new() { Stretch = Stretch.None };
    private string _previewFolder = "";
    private readonly ViewportTools _tools;
    private readonly SliceEditor _slice = new();
    private readonly Onboarding _onboarding;
    private readonly ColumnDefinition _col3d = new(), _col2d = new() { Width = new GridLength(0) };
    private readonly Dictionary<int, ToggleButton> _modeButtons = new();
    private Button? _welcomeImportTile;
    private FrameworkElement? _toolbarHost;
    private Border? _viewportFrame;
    private Grid? _sidePanel;
    private Button? _helpButton;
    /// <summary>视图模式：0 = 三维，1 = 分层，2 = 并排。</summary>
    private int _viewMode;
    private CancellationTokenSource? _busyCts;
    private bool _busyNow;

    private readonly IStepPage[] _pages;
    private int _step = 1;

    public MainWindow()
    {
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = Theme.AppName;
        ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var caption = AppWindow.TitleBar;
            caption.ButtonBackgroundColor = Colors.Transparent;
            caption.ButtonInactiveBackgroundColor = Colors.Transparent;
            caption.ButtonForegroundColor = Theme.Text1;
            caption.ButtonInactiveForegroundColor = Theme.Text3;
            caption.ButtonHoverBackgroundColor = Theme.Orange;
            caption.ButtonHoverForegroundColor = Theme.Navy;
            caption.ButtonPressedBackgroundColor = Theme.OrangeDeep;
            caption.ButtonPressedForegroundColor = Theme.Navy;
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1680, area.Width - 40), h = Math.Min(1020, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

        S = new Session();
        Accounts = new AccountManager(S);
        _undo = IconBtn("", "撤销 (Ctrl+Z)", () => S.History.Undo());
        _redo = IconBtn("", "重做 (Ctrl+Y)", () => S.History.Redo());
        _tools = new ViewportTools(this);

        _pages = new IStepPage[]
        {
            new ImportPage(this), new EditPage(this), new PartsPage(this), new CheckPage(this), new ExportPage(this), new SettingsPage(this),
        };

        BuildLayout();
        _onboarding = new Onboarding(this, new[]
        {
            new TourStop("从照片开始", "上传 3~6 张同一物体的照片，自动生成三维拼豆模型。也可以打开已有工程，或从空白画布搭起。",
                () => _welcomeImportTile, "第一次使用，从这里进入导入页。", false),
            new TourStop("五步制作流程", "导入 → 编辑 → 拆分 → 检查 → 导出，左侧按顺序走完就是一件可施工的作品。随时可以点回上一步。",
                () => _nav.MenuItems.FirstOrDefault() as FrameworkElement, "五步各有独立页面。"),
            new TourStop("动作与笔刷", "数字键 1~5 切换贴豆、擦除、上色、零件、选择；字母键切换笔刷。按住拖动可以连续绘制。",
                () => _toolbarHost, "工具栏跟随当前动作，镜像和当前色也在这里。"),
            new TourStop("三维视口", "右键拖动旋转，中键或空格+左键平移，滚轮缩放；F 居中，Alt+左键吸色。右上角的「正 / 侧 / 背 / 顶 / 斜」可以快速回到标准视角。",
                () => _viewportFrame, "所有修形都在这里直接完成。"),
            new TourStop("三维 / 分层 / 并排", "三维看整体，分层逐格修形，并排视图两边同步。做平面零件时切到分层最方便。",
                () => _modeBar, "顶部的视图切换。"),
            new TourStop("右侧参数面板", "当前步骤的设置、AI 状态、颜色与操作按钮都在这里，会随左侧流程自动切换。",
                () => _sidePanel, "不需要时会自动收起内容。"),
            new TourStop("随时重看", "右上角 ? 打开快捷键与使用帮助，也可以重看这次导览。",
                () => _helpButton, "不需要记住所有快捷键，用到时再查。"),
        }, () => { _welcomeDismissed = true; UpdateWelcome(); }, () =>
        {
            if (S.Model.CountSolid() == 0 && S.ProjectPath == null)
            {
                _welcomeDismissed = false;
                UpdateWelcome();
            }
        });
        Grid.SetRowSpan(_onboarding, 2);
        _root.Children.Add(_onboarding);
        View.Attach(S);
        _slice.Attach(S);
        _slice.Message += m => Notify(m);
        View.StatusChanged += (stats, hover) => { _stats.Text = stats; _hover.Text = hover ?? ""; };
        View.Message += m => Notify(m, InfoBarSeverity.Warning);
        S.Status += m => DispatcherQueue.TryEnqueue(() => Notify(m));
        S.ModelChanged += () => DispatcherQueue.TryEnqueue(UpdateChrome);
        S.ModelReplaced += () => DispatcherQueue.TryEnqueue(() => { UpdateChrome(); UpdateWelcome(); });
        S.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Session.Dirty)) DispatcherQueue.TryEnqueue(UpdateChrome); };
        _root.Background = Theme.Background();
        Content = _root;
        SetTitleBar(_dragRegion);
        Closed += OnClosed;
        AppWindow.Closing += OnClosing;
        _root.Loaded += async (_, _) =>
        {
            GoStep(1);
            _ = Accounts.RestoreAsync();
            UpdateChrome();
            UpdateWelcome();
            if (!_onboarding.Done) _onboarding.Show();   // 第一次打开：先看使用引导
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await OpenAnyAsync(args[1]);
            var local = Hy3dClient.IsInstalled(out _)
                ? "本地混元3D 就绪。"
                : Hy3dClient.CutoutInstalled
                    ? "本地抠图就绪；混元3D 未安装，可在「设置」一键安装。"
                    : "本地 AI 未安装，可在「设置」一键安装；三维重建将使用 CPU 轮廓雕刻。";
            Notify($"已加载 {S.Lib.Brands.Count} 套色卡。" + local, InfoBarSeverity.Informational);
        };
    }

    // ═════════════════ 布局 ═════════════════

    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition());

        // 标题栏：左侧可拖动区域 + 右侧操作按钮（避开系统按钮）
        var bar = new Grid { Padding = new Thickness(14, 0, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var drag = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.png"))), Width = 22, Height = 22 });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new SkewTransform { AngleX = -10 } };
        name.Children.Add(new TextBlock { Text = Theme.AppName, FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new TextBlock { Text = Theme.AppNameEn, FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, CharacterSpacing = 200, Foreground = Theme.B(Theme.Orange), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
        brand.Children.Add(name);
        brand.Children.Add(_projectName);
        drag.Children.Add(brand);
        Grid.SetColumnSpan(drag, 2);
        bar.Children.Add(drag);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(IconBtn("", "新建空白模型 (Ctrl+N)", async () => await NewBlankAsync()));
        actions.Children.Add(IconBtn("", "打开工程 / 模型 (Ctrl+O)", async () => await OpenDialogAsync()));
        actions.Children.Add(IconBtn("", "保存工程 (Ctrl+S)", async () => await SaveAsync(false)));
        actions.Children.Add(new AppBarSeparator());
        actions.Children.Add(_undo);
        actions.Children.Add(_redo);
        actions.Children.Add(new AppBarSeparator());
        actions.Children.Add(IconBtn(G.Account, "账号与云端同步", () => OpenAccount()));
        var help = IconBtn("", "使用帮助与快捷键", async () => await ShowHelpAsync());
        _helpButton = help;
        actions.Children.Add(help);
        Grid.SetColumn(actions, 2);
        bar.Children.Add(actions);
        _root.Children.Add(bar);
        _dragRegion = drag;

        // 导航
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = true;
        _nav.OpenPaneLength = 214;
        _nav.IsPaneOpen = true;
        _nav.PaneTitle = "制作流程";
        var items = new (string Text, string Glyph, string Tip)[]
        {
            ("导入 · 图片", "", "多视图 → 三维重建"),
            ("编辑 · 模型", "", "贴豆 · 擦除 · 上色"),
            ("拆分 · 零件", "", "分层 · 面板 · 齿口"),
            ("检查 · 问题", "", "漏色 · 装配验证"),
            ("导出 · 图纸", "", "颗粒图 · 爆炸图 · 清单"),
        };
        for (int i = 0; i < items.Length; i++)
        {
            var it = new NavigationViewItem { Content = items[i].Text, Icon = new FontIcon { Glyph = items[i].Glyph }, Tag = i + 1 };
            ToolTipService.SetToolTip(it, items[i].Tip);
            _nav.MenuItems.Add(it);
        }
        _nav.MenuItems.Add(new NavigationViewItemSeparator());
        var flatItem = new NavigationViewItem
        {
            Content = "平面 · 拼豆", Tag = 7, SelectsOnInvoked = false,
            Icon = new FontIcon { Glyph = G.Grid },
        };
        ToolTipService.SetToolTip(flatItem, "单图 → AI 生图 → 像素化 → 平面图纸");
        _nav.MenuItems.Add(flatItem);
        _nav.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItemContainer is NavigationViewItem { Tag: 7 }) OpenFlatPattern();
        };
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.IsSettingsSelected) { ShowPage(6); return; }
            if (e.SelectedItem is NavigationViewItem { Tag: int step }) ShowPage(step);
        };
        Grid.SetRow(_nav, 1);
        _root.Children.Add(_nav);

        // 内容：视口 + 右侧面板
        var content = new Grid { Padding = new Thickness(0, 0, 12, 12), ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(384) });
        content.Children.Add(BuildViewportArea());

        var side = new Grid { RowSpacing = 4 };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition());
        var head = new StackPanel { Padding = new Thickness(4, 6, 4, 6) };
        head.Children.Add(_panelTitle);
        head.Children.Add(_panelDesc);
        head.Children.Add(_panelLine);
        side.Children.Add(head);
        Grid.SetRow(_info, 1);
        side.Children.Add(_info);
        var scroll = new ScrollViewer { Content = _panelHost, Padding = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(scroll, 2);
        side.Children.Add(scroll);
        Grid.SetColumn(side, 1);
        content.Children.Add(side);
        _sidePanel = side;
        _nav.Content = content;

        // 快捷键与拖放
        _root.KeyDown += OnKeyDown;
        _root.KeyUp += (_, e) => { if (e.Key == VirtualKey.Space) View.SetSpace(false); };
        _root.AllowDrop = true;
        _root.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "导入到" + Theme.AppName; };
        _root.Drop += OnDrop;
    }

    private UIElement BuildViewportArea()
    {
        var outer = new Grid { ColumnSpacing = Theme.S3 };
        outer.ColumnDefinitions.Add(_col3d);
        outer.ColumnDefinitions.Add(_col2d);
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition());
        var area = new Grid();
        Grid.SetRow(area, 1);
        outer.Children.Add(area);
        var sliceFrame = new Border { Child = _slice, CornerRadius = Theme.RCard, Background = Theme.B(Theme.Surface3), Visibility = Visibility.Collapsed };
        Grid.SetColumn(sliceFrame, 1);
        Grid.SetRow(sliceFrame, 1);
        outer.Children.Add(sliceFrame);
        _sliceFrame = sliceFrame;
        _area3d = area;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(3, 18, 3, 18),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Theme.Orange, Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0.3 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Offset = 0.7 },
                    new GradientStop { Color = Theme.Blue, Offset = 1 },
                },
            },
            BorderThickness = new Thickness(1.5),
            Child = View,
            Background = Solid(0x1D, 0x1F, 0x23),
        };
        area.Children.Add(frame);
        _viewportFrame = frame;

        // 浮动工具栏（左）
        var toolbar = _tools.BuildToolbar();
        toolbar.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Margin = new Thickness(12, 0, 0, 0);
        area.Children.Add(toolbar);
        _toolbarHost = toolbar;

        // 视角（右上）
        var views = _tools.BuildViewBar();
        views.HorizontalAlignment = HorizontalAlignment.Right;
        views.VerticalAlignment = VerticalAlignment.Top;
        views.Margin = new Thickness(0, 12, 12, 0);
        area.Children.Add(views);

        // 状态（下）
        var status = new Border
        {
            Background = Theme.B(Theme.Navy, 0xE6), CornerRadius = new CornerRadius(2, 12, 2, 12), BorderBrush = Theme.B(Theme.Orange, 0x80), BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 12),
            Child = HStack(_stats, _hover),
        };
        area.Children.Add(status);
        var hint = new TextBlock
        {
            Text = "右键拖动 旋转 · 中键/空格+左键 平移 · 滚轮 缩放 · Alt+左键 吸色 · F 居中",
            FontSize = 11.5, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 16, 46),
        };
        area.Children.Add(hint);


        // 视图模式切换（顶部居中）
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var (i, label, tip) in new[] { (0, "三维", "只看三维视图"), (1, "分层", "像画图纸一样逐层编辑（[ ] 换层）"), (2, "并排", "三维与分层并排，改哪边另一边同步") })
        {
            var t = TextToggle(label, tip, 56);
            t.Margin = new Thickness(0);
            t.CornerRadius = i == 0 ? new CornerRadius(4, 0, 0, 4) : i == 2 ? new CornerRadius(0, 4, 4, 0) : new CornerRadius(0);
            t.Click += (_, _) => SetViewMode(i);
            _modeButtons[i] = t;
            modes.Children.Add(t);
        }
        var modeBar = new Border
        {
            Child = modes, Background = Theme.B(Theme.Navy, 0xEE), CornerRadius = Theme.RSmall, Padding = new Thickness(Theme.S1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, Theme.S2),
        };
        _modeBar = modeBar;
        outer.Children.Add(modeBar);
        Grid.SetColumnSpan(modeBar, 2);
        SetViewMode(0);

        // 欢迎页 / 图纸预览 / 读条 覆盖视口区（无论三维还是分层）
        BuildWelcome();
        BuildPreview();
        BuildBusy();
        foreach (var o in new FrameworkElement[] { _welcome, _preview, _busy })
        {
            Grid.SetColumnSpan(o, 2);
            Grid.SetRow(o, 1);
            outer.Children.Add(o);
        }
        return outer;
    }

    private void BuildWelcome()
    {
        _welcome.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xEE, 0x1A, 0x24, 0x36), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0xF6, 0x0E, 0x14, 0x1F), Offset = 1 },
            },
        };
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12, MaxWidth = 720, Padding = new Thickness(20) };
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        logo.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.png"))), Width = 76, Height = 76 });
        var big = Theme.Heading(Theme.AppName, Theme.AppNameEn, 46);
        big.VerticalAlignment = VerticalAlignment.Center;
        logo.Children.Add(big);
        sp.Children.Add(logo);
        sp.Children.Add(new TextBlock { Text = Theme.Slogan + "！", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 15, Foreground = Theme.B(Theme.Orange), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        sp.Children.Add(new TextBlock
        {
            Text = "上传 3~6 张同一物体的照片重建模型，手工修形、拆成零件，再生成整套图纸。",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush"),
        });
        var choose = Theme.Heading("选择开始方式", "START A PROJECT", 22);
        choose.HorizontalAlignment = HorizontalAlignment.Center;
        choose.Margin = new Thickness(0, 14, 0, 0);
        sp.Children.Add(choose);
        var grid = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Tile(int col, string role, Windows.UI.Color roleColor, string glyph, string title, string sub, Action act)
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                CornerRadius = new CornerRadius(3, 18, 3, 18), Background = Theme.B(Theme.Navy, 0xCC), BorderBrush = Theme.B(roleColor, 0x90), BorderThickness = new Thickness(1.5),
            };
            var c = new Grid { Padding = new Thickness(16, 14, 16, 16), RowSpacing = 8, MinHeight = 190 };
            c.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            c.RowDefinitions.Add(new RowDefinition());
            c.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            c.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            c.Children.Add(Theme.Tag(role, roleColor, 12, Microsoft.UI.Colors.White));
            var ic = Icon(glyph, 44);
            ic.Foreground = Theme.B(roleColor);
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(ic, 1);
            c.Children.Add(ic);
            var t = new TextBlock { Text = title, FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = new SkewTransform { AngleX = -8 } };
            Grid.SetRow(t, 2);
            c.Children.Add(t);
            var st = new TextBlock { Text = sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") };
            Grid.SetRow(st, 3);
            c.Children.Add(st);
            b.Content = c;
            b.Click += (_, _) => act();
            if (col == 0) _welcomeImportTile = b;
            Grid.SetColumn(b, col);
            grid.Children.Add(b);
        }
        Tile(0, "导入 · IMPORT", Theme.Orange, G.Photo, "从图片开始", "照片 → AI 视图 → 三维重建", () => { _welcomeDismissed = true; UpdateWelcome(); GoStep(1); });
        Tile(1, "打开 · OPEN", Theme.Blue, G.Open, "打开作品", ".doudou / .vox / 三维网格", async () => await OpenDialogAsync());
        Tile(2, "新建 · NEW", Theme.Green, G.Cube, "空白画布", "从零搭建体素模型", async () => await NewBlankAsync());
        sp.Children.Add(grid);

        var flatText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        flatText.Children.Add(new TextBlock
        {
            Text = "平面拼豆 · FLAT PATTERN", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 16, RenderTransform = new SkewTransform { AngleX = -8 },
        });
        flatText.Children.Add(new TextBlock
        {
            Text = "上传 1 张图 → AI 卡通平涂 → 像素化 → 导出带色号的平面图纸",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextFillColorSecondaryBrush"),
        });
        var flatContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var flatIcon = Icon(G.Grid, 26);
        flatIcon.Foreground = Theme.B(Theme.Blue);
        flatIcon.VerticalAlignment = VerticalAlignment.Center;
        flatContent.Children.Add(flatIcon);
        flatContent.Children.Add(flatText);
        var flatTile = new Button
        {
            Content = flatContent, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 11, 16, 11),
            Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(3, 14, 3, 14),
            Background = Theme.B(Theme.Navy, 0xCC), BorderBrush = Theme.B(Theme.Blue, 0x70), BorderThickness = new Thickness(1.2),
        };
        flatTile.Click += (_, _) => OpenFlatPattern();
        sp.Children.Add(flatTile);

        var recent = RecentFiles.Load();
        if (recent.Count > 0)
        {
            var rh = Theme.Heading("最近作品", "RECENT", 16);
            rh.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(rh);
            foreach (var r in recent.Take(5))
            {
                var link = new HyperlinkButton { Content = Path.GetFileName(r), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Theme.B(Theme.Orange) };
                ToolTipService.SetToolTip(link, r);
                link.Click += async (_, _) => await OpenAnyAsync(r);
                sp.Children.Add(link);
            }
        }
        _welcome.Children.Add(new ScrollViewer { Content = sp, VerticalAlignment = VerticalAlignment.Center });
    }

    private bool _welcomeDismissed;
    private Border? _sliceFrame, _modeBar;
    private Grid? _area3d;

    /// <summary>三维 / 分层 / 并排。</summary>
    private void SetViewMode(int mode)
    {
        _viewMode = mode;
        foreach (var (k, b) in _modeButtons) b.IsChecked = k == mode;
        _col3d.Width = mode == 1 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _col2d.Width = mode == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (_area3d != null) _area3d.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (_sliceFrame != null) _sliceFrame.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        _slice.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        _slice.SetActive(mode != 0);
        View.Invalidate();
    }
    private UIElement? _dragRegion;

    private void UpdateWelcome()
    {
        bool empty = S.Model.CountSolid() == 0 && S.ProjectPath == null;
        bool show = empty && !_welcomeDismissed && _step == 1;
        _welcome.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_modeBar != null) _modeBar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildPreview()
    {
        _preview.Background = Solid(0x17, 0x18, 0x1B);
        _preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _preview.RowDefinitions.Add(new RowDefinition());
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12) };
        top.Children.Add(new TextBlock { Text = "图纸预览", Style = Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        top.Children.Add(_previewPick);
        top.Children.Add(Btn("适合宽度", FitPreview, ""));
        top.Children.Add(Btn("打开文件夹", () => { if (Directory.Exists(_previewFolder)) System.Diagnostics.Process.Start("explorer.exe", _previewFolder); }, ""));
        top.Children.Add(Btn("返回三维视图", () => _preview.Visibility = Visibility.Collapsed, "", accent: true));
        _preview.Children.Add(top);
        _previewScroll.Content = _previewImage;
        Grid.SetRow(_previewScroll, 1);
        _preview.Children.Add(_previewScroll);
        _previewPick.SelectionChanged += (_, _) => LoadPreview();
    }

    private readonly TextBlock _busyTip = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 440, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>读条界面：终极技能充能圈 + 任务进度 + 轮播「语音」提示。</summary>
    private void BuildBusy()
    {
        _busy.Background = Theme.B(Windows.UI.Color.FromArgb(255, 0x08, 0x0C, 0x14), 0xD0);
        var card = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(36, 28, 36, 28), MinWidth = 420 };
        var ult = new Grid { Width = 92, Height = 92, HorizontalAlignment = HorizontalAlignment.Center };
        ult.Children.Add(new ProgressRing { IsActive = true, Width = 92, Height = 92, Foreground = Theme.B(Theme.Orange) });
        ult.Children.Add(new TextBlock { Text = "ULT", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Black, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.B(Theme.Orange) });
        card.Children.Add(ult);
        card.Children.Add(new TextBlock { Text = "终极技能充能中", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, CharacterSpacing = 120, Foreground = Theme.B(Theme.Orange) });
        _busyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _busyTitle.FontFamily = Theme.Display;
        _busyTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _busyTitle.FontSize = 24;
        _busyTitle.RenderTransform = new SkewTransform { AngleX = -10 };
        _busyText.HorizontalAlignment = HorizontalAlignment.Center;
        _busyText.TextAlignment = TextAlignment.Center;
        card.Children.Add(_busyTitle);
        card.Children.Add(_busyText);
        card.Children.Add(new ProgressBar { IsIndeterminate = true, Foreground = Theme.B(Theme.Blue), Margin = new Thickness(0, 4, 0, 4) });
        card.Children.Add(new Border
        {
            Child = _busyTip, Padding = new Thickness(12, 8, 12, 8), Background = Theme.B(Theme.Blue, 0x22),
            BorderBrush = Theme.B(Theme.Blue, 0x88), BorderThickness = new Thickness(3, 0, 0, 0),
        });
        card.Children.Add(_busyCancel);
        _busyCancel.Click += (_, _) => { _busyCts?.Cancel(); _busyText.Text = "正在撤退…"; };
        _tipTimer.Tick += (_, _) => _busyTip.Text = Theme.RandomTip();
        _busy.Children.Add(new Border
        {
            Child = card, Background = Theme.B(Theme.Navy, 0xF2), CornerRadius = new CornerRadius(3, 22, 3, 22),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Theme.B(Theme.Orange, 0xA0), BorderThickness = new Thickness(1.5),
        });
    }

    // ═════════════════ 步骤 ═════════════════

    public void GoStep(int step)
    {
        if (step == 6) { _nav.SelectedItem = _nav.SettingsItem; return; }
        var item = _nav.MenuItems[step - 1];
        // 已经在这一页时 SelectedItem 赋值不会触发 SelectionChanged，要自己走一遍（OnEnter 才会刷新）
        if (ReferenceEquals(_nav.SelectedItem, item)) ShowPage(step);
        else _nav.SelectedItem = item;
    }

    private void ShowPage(int step)
    {
        if (_step != step && _step >= 1 && _step <= _pages.Length) _pages[_step - 1].OnLeave();
        _step = step;
        var page = _pages[step - 1];
        _panelTitle.Text = page.Header;
        _panelDesc.Text = page.Description;
        _panelLine.Text = step <= 5 ? "“" + Theme.StepLine(step) + "”" : "";
        _panelHost.Content = page.View;
        _preview.Visibility = Visibility.Collapsed;
        if (step != 3 && S.Explode > 1.01f) S.Explode = 1;
        if (step is 1 or 2 or 5) S.Display = DisplayMode.Color;
        if (step == 2 && S.Action == EditAction.PartPaint) S.Action = EditAction.Attach;
        _tools.Sync();
        page.OnEnter();
        UpdateWelcome();
    }

    // ═════════════════ IHost ═════════════════

    public void Notify(string msg, InfoBarSeverity sev = InfoBarSeverity.Informational)
    {
        _info.Severity = sev;
        _info.Title = sev switch
        {
            InfoBarSeverity.Success => "胜利！",
            InfoBarSeverity.Warning => "注意！",
            InfoBarSeverity.Error => "失败",
            _ => "",
        };
        _info.Message = msg;
        _info.IsOpen = true;
        if (sev == InfoBarSeverity.Informational || sev == InfoBarSeverity.Success)
        {
            var stamp = msg;
            _ = Task.Delay(6000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => { if (_info.Message == stamp) _info.IsOpen = false; }));
        }
    }

    public void ShowError(string msg) => DispatcherQueue.TryEnqueue(() => Notify(msg, InfoBarSeverity.Error));

    public async Task RunAsync(string title, Func<IProgress<string>, CancellationToken, Task> work, bool cancellable = false)
    {
        if (_busyNow) { Notify("请等待当前任务完成", InfoBarSeverity.Warning); return; }
        _busyNow = true;
        _busyCts = new CancellationTokenSource();
        _busyTitle.Text = title;
        _busyText.Text = "比赛即将开始…";
        _busyTip.Text = Theme.RandomTip();
        _tipTimer.Start();
        _busyCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        _busy.Visibility = Visibility.Visible;
        _panelHost.IsEnabled = false;
        _nav.IsEnabled = false;   // 任务进行中不允许换页/新建，否则结果会盖掉用户刚打开的东西
        var progress = new Progress<string>(s => _busyText.Text = s);
        try
        {
            await work(progress, _busyCts.Token);
        }
        catch (OperationCanceledException) { Notify(title + "已取消", InfoBarSeverity.Warning); }
        catch (Exception e)
        {
            Notify(title + "失败：" + e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busyNow = false;
            _tipTimer.Stop();
            _busy.Visibility = Visibility.Collapsed;
            _panelHost.IsEnabled = true;
            _nav.IsEnabled = true;
            _busyCts.Dispose();
            _busyCts = null;
        }
    }

    public void OpenFlatPattern()
    {
        if (_flatWindow != null) { _flatWindow.Activate(); return; }
        _flatWindow = new FlatPatternWindow(S, Accounts);
        _flatWindow.Closed += (_, _) => _flatWindow = null;
        _flatWindow.Activate();
    }

    public void OpenAccount()
    {
        if (_accountWindow != null) { _accountWindow.Activate(); return; }
        _accountWindow = new AccountWindow(S, Accounts);
        _accountWindow.Closed += (_, _) => _accountWindow = null;
        _accountWindow.Activate();
    }

    public void ShowPreview(IReadOnlyList<string> images, string folder)
    {
        if (images.Count == 0) { Notify("没有可预览的图片", InfoBarSeverity.Warning); return; }
        _previewFolder = folder;
        _previewPick.Tag = images;
        _previewPick.ItemsSource = images.Select(Path.GetFileNameWithoutExtension).ToList();
        _preview.Visibility = Visibility.Visible;
        _previewPick.SelectedIndex = 0;
    }

    private void LoadPreview()
    {
        if (_previewPick.Tag is not IReadOnlyList<string> files || _previewPick.SelectedIndex < 0) return;
        if (_previewPick.SelectedIndex >= files.Count) return;
        // 用文件流解码：路径里的 # % 空格等字符走 Uri 会被转义错，图就打不开了
        var bmp = new BitmapImage { DecodePixelWidth = 4200 };
        bmp.ImageOpened += (_, _) => FitPreview();
        _ = LoadAsync(bmp, files[_previewPick.SelectedIndex]);
        _previewImage.Source = bmp;
    }

    private async Task LoadAsync(BitmapImage bmp, string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
        }
        catch (Exception e) { Notify("预览失败：" + e.Message, InfoBarSeverity.Error); }
    }

    private void FitPreview()
    {
        if (_previewImage.Source is not BitmapImage b || b.PixelWidth == 0) return;
        float z = (float)Math.Clamp((_previewScroll.ActualWidth - 24) / b.PixelWidth, 0.1, 4);
        _previewScroll.ChangeView(0, 0, z, true);
    }

    public async Task<string?> PromptAsync(string title, string label, string def)
    {
        var tb = new TextBox { Text = def, Header = label };
        var dlg = new ContentDialog { Title = title, Content = tb, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? tb.Text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string text, string ok = "确定", string cancel = "取消")
    {
        var dlg = new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = ok, CloseButtonText = cancel, DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    // ═════════════════ 文件 ═════════════════

    private void UpdateChrome()
    {
        var name = S.ProjectPath != null ? Path.GetFileName(S.ProjectPath) : S.Meta.Title;
        _projectName.Text = name + (S.Dirty ? "  ●" : "");
        Title = $"{Theme.AppName} · {name}";
        _undo.IsEnabled = S.History.CanUndo;
        _redo.IsEnabled = S.History.CanRedo;
        ToolTipService.SetToolTip(_undo, S.History.CanUndo ? $"撤销：{S.History.UndoName} (Ctrl+Z)" : "撤销 (Ctrl+Z)");
        ToolTipService.SetToolTip(_redo, S.History.CanRedo ? $"重做：{S.History.RedoName} (Ctrl+Y)" : "重做 (Ctrl+Y)");
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!S.Dirty || S.Model.CountSolid() == 0) return true;
        var dlg = new ContentDialog
        {
            Title = "保存修改？", Content = "当前作品有未保存的修改。", PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.None) return false;
        if (r == ContentDialogResult.Primary) return await SaveAsync(false);
        return true;
    }

    private async Task NewBlankAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var x = new NumberBox { Header = "宽 X", Value = 32, Minimum = 1, Maximum = 256, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var y = new NumberBox { Header = "高 Y", Value = 40, Minimum = 1, Maximum = 256, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var z = new NumberBox { Header = "深 Z", Value = 32, Minimum = 1, Maximum = 256, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var g = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(y, 1); Grid.SetColumn(z, 2);
        g.Children.Add(x); g.Children.Add(y); g.Children.Add(z);
        var sp = new StackPanel { Spacing = 10 };
        sp.Children.Add(Caption("画布尺寸（颗）。豆径 2.6mm，40 颗高约 10.4cm。"));
        sp.Children.Add(g);
        var dlg = new ContentDialog { Title = "新建空白模型", Content = sp, PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        S.NewBlank(Clamp(x.Value), Clamp(y.Value), Clamp(z.Value));
        _welcomeDismissed = true;
        GoStep(2);
        static int Clamp(double v) => double.IsNaN(v) ? 32 : Math.Clamp((int)v, 1, 256);
    }

    private async Task OpenDialogAsync()
    {
        var p = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
        foreach (var e in new[] { ".doudou", ".vox", ".obj", ".glb", ".stl" }) p.FileTypeFilter.Add(e);
        var f = await p.PickSingleFileAsync();
        if (f != null) await OpenAnyAsync(f.Path);
    }

    private async Task OpenAnyAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".doudou") { await OpenProjectAsync(path); return; }
        if (!await ConfirmDiscardAsync()) return;
        if (ext == ".vox")
        {
            try
            {
                var m = VoxFile.Read(path);
                Quantizer.RemapToBrand(m, S.Lib, S.Settings.DefaultBrand);
                S.Replace(m, new ProjectMeta { Title = Path.GetFileNameWithoutExtension(path) }, new());
                RecordOpen(Path.GetFileNameWithoutExtension(path), m, "vox");
                _welcomeDismissed = true;
                RecentFiles.Add(path);
                Notify($"已导入 {m.CountSolid():N0} 颗，颜色已映射到 {S.Lib.Get(m.BrandId).Title}", InfoBarSeverity.Success);
                GoStep(2);
            }
            catch (Exception e) { Notify("导入失败：" + e.Message, InfoBarSeverity.Error); }
            return;
        }
        if (ext is ".obj" or ".glb" or ".stl")
        {
            var hs = await PromptAsync("导入三维网格", "成品高度（颗 / 层）", "48");
            if (!int.TryParse(hs, out int h) || h < 5) return;
            await RunAsync("导入三维模型", async (prog, ct) =>
            {
                var res = await Task.Run(() =>
                {
                    var mesh = MeshLoader.Load(path);
                    return MeshImporter.Import(mesh, S.Lib, new MeshImportOptions
                    {
                        TargetHeight = h,
                        BrandId = S.Settings.DefaultBrand,
                        MaxColors = 32,
                        Name = Path.GetFileNameWithoutExtension(path),
                    }, m => prog.Report(m), ct);
                }, ct);
                var m = res.Model;
                S.Replace(m, new ProjectMeta
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    SourceMesh = path,
                    SourceHeight = h,
                    SourceBrand = S.Settings.DefaultBrand,
                    SourceMaxColors = 32,
                }, new());
                RecordOpen(Path.GetFileNameWithoutExtension(path), m, "mesh");
                _welcomeDismissed = true;
                var how = res.UsedTexture ? "模型自带纹理上色" : "单色（无纹理/参考图，请在编辑中上色）";
                Notify($"网格已体素化：{m.SX}×{m.SY}×{m.SZ}，{m.CountSolid():N0} 颗；{how}{(res.RotatedZUp ? "；Z-up 已转正" : "")}", InfoBarSeverity.Success);
                GoStep(2);
            });
        }
    }

    private void RecordOpen(string title, Core.Model.VoxelModel model, string mode)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "未命名" : title.Trim();
        if (name.Length > 120) name = name[..120];
        Accounts.RecordHistory(new HistoryEntry
        {
            Kind = "open",
            Title = name,
            Summary = $"{model.CountSolid():N0} 颗 · {model.PartList.Count} 个零件 · {model.Palette.Count - 1} 色",
            Meta = new HistoryMeta
            {
                Beads = model.CountSolid(),
                Colors = model.Palette.Count - 1,
                Parts = model.PartList.Count,
                Size = $"{model.SX}×{model.SY}×{model.SZ}",
                Source = mode,
            },
        });
    }

    public async Task OpenProjectAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return;
        try
        {
            var dir = AppPaths.NewWorkDir("open");
            var (m, meta, views) = ProjectFile.Load(path, dir);
            S.Replace(m, meta, views, path);
            RecordOpen(meta.Title, m, "project");
            _welcomeDismissed = true;
            RecentFiles.Add(path);
            Notify($"已打开 {Path.GetFileName(path)}：{m.CountSolid():N0} 颗，{m.PartList.Count} 个零件", InfoBarSeverity.Success);
            GoStep(2);
        }
        catch (Exception e) { Notify("打开失败：" + e.Message, InfoBarSeverity.Error); }
    }

    public async Task<bool> SaveAsync(bool saveAs)
    {
        var path = S.ProjectPath;
        if (saveAs || path == null)
        {
            var p = new FileSavePicker { SuggestedFileName = S.Meta.Title };
            WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
            p.FileTypeChoices.Add(Theme.AppName + "工程", new List<string> { ".doudou" });
            var f = await p.PickSaveFileAsync();
            if (f == null) return false;
            path = f.Path;
        }
        try
        {
            ProjectFile.Save(path, S.Model, S.Meta, S.ViewFiles);
            S.ProjectPath = path;
            S.Dirty = false;
            RecentFiles.Add(path);
            UpdateChrome();
            Notify("已保存 " + path, InfoBarSeverity.Success);
            return true;
        }
        catch (Exception e)
        {
            Notify("保存失败：" + e.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    public async Task ExportVoxAsync()
    {
        var p = new FileSavePicker { SuggestedFileName = S.Meta.Title };
        WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
        p.FileTypeChoices.Add("MagicaVoxel", new List<string> { ".vox" });
        var f = await p.PickSaveFileAsync();
        if (f == null) return;
        try { VoxFile.Write(S.Model, f.Path); Notify("已导出 " + f.Path, InfoBarSeverity.Success); }
        catch (Exception e) { Notify("导出失败：" + e.Message, InfoBarSeverity.Error); }
    }

    private async Task ShowHelpAsync()
    {
        var text =
            "【视口】右键拖动＝旋转；中键拖动或 空格+左键＝平移；滚轮＝缩放；Alt+左键＝吸色；F＝居中\n\n" +
            "【动作】1 贴豆　2 擦除　3 上色　4 零件刷　5 选择\n" +
            "【笔刷】V 单格　G 整面　B 矩形　L 直线　R 填充　P 平面\n" +
            "【镜像】Shift+X / Shift+Y / Shift+Z\n" +
            "【剖切】PageUp / PageDown 逐层；Home 关闭剖切\n" +
            "【选择】Shift 加选，Ctrl 减选；Delete 删除所选；Esc 取消\n" +
            "【文件】Ctrl+N 新建　Ctrl+O 打开　Ctrl+S 保存　Ctrl+Z/Y 撤销/重做\n\n" +
            "流程：① 导入图片重建 → ② 编辑修形上色 → ③ 拆成可熨烫的平板零件 → ④ 检查漏色与装配 → ⑤ 导出图纸。\n\n" +
            "平面拼豆：左侧「平面 · 拼豆」→ 上传 1 张图 → AI 卡通平涂 → 像素化 → 导出 PNG / PDF / 材料清单。";
        var dlg = new ContentDialog
        {
            Title = "使用帮助", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "重看新手引导", CloseButtonText = "知道了", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) _onboarding.Show(false);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;
        var ext = Path.GetExtension(paths[0]).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
        {
            _welcomeDismissed = true;
            GoStep(1);
            ((ImportPage)_pages[0]).AddImages(paths);
        }
        else await OpenAnyAsync(paths[0]);
    }

    // ═════════════════ 快捷键 ═════════════════

    private static bool KeyDown(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or NumberBox or PasswordBox or AutoSuggestBox) return;
        bool ctrl = KeyDown(VirtualKey.Control), shift = KeyDown(VirtualKey.Shift);
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Z when shift: S.History.Redo(); e.Handled = true; return;
                case VirtualKey.Z: S.History.Undo(); e.Handled = true; return;
                case VirtualKey.Y: S.History.Redo(); e.Handled = true; return;
                case VirtualKey.S: e.Handled = true; await SaveAsync(shift); return;
                case VirtualKey.O: e.Handled = true; await OpenDialogAsync(); return;
                case VirtualKey.N: e.Handled = true; await NewBlankAsync(); return;
            }
            return;
        }
        if (shift)
        {
            switch (e.Key)
            {
                case VirtualKey.X: S.MirrorX = !S.MirrorX; break;
                case VirtualKey.Y: S.MirrorY = !S.MirrorY; break;
                case VirtualKey.Z: S.MirrorZ = !S.MirrorZ; break;
                default: return;
            }
            _tools.Sync(); e.Handled = true; return;
        }
        switch (e.Key)
        {
            case VirtualKey.Space: View.SetSpace(true); break;
            case VirtualKey.F: View.FrameModel(); break;
            case VirtualKey.C: S.CheckMode = !S.CheckMode; break;
            case VirtualKey.H: S.HideSelection = !S.HideSelection; break;
            case VirtualKey.Number1: S.Action = EditAction.Attach; break;
            case VirtualKey.Number2: S.Action = EditAction.Erase; break;
            case VirtualKey.Number3: S.Action = EditAction.Paint; break;
            case VirtualKey.Number4: S.Action = EditAction.PartPaint; break;
            case VirtualKey.Number5: S.Action = EditAction.Select; break;
            case VirtualKey.V: S.Brush = BrushMode.Voxel; break;
            case VirtualKey.G: S.Brush = BrushMode.Face; break;
            case VirtualKey.B: S.Brush = BrushMode.Box; break;
            case VirtualKey.L: S.Brush = BrushMode.Line; break;
            case VirtualKey.R: S.Brush = BrushMode.Fill; break;
            case VirtualKey.P: S.Brush = BrushMode.Plane; break;
            case VirtualKey.Escape: View.Tools?.Cancel(); S.Selection.Clear(); S.RaiseChanged(); break;
            case VirtualKey.Delete: ((EditPage)_pages[1]).DeleteSelection(); break;
            case VirtualKey.PageUp when _viewMode != 0: _slice.SetLayer(_slice.Layer + 1); break;
            case VirtualKey.PageDown when _viewMode != 0: _slice.SetLayer(_slice.Layer - 1); break;
            case (VirtualKey)221 when _viewMode != 0: _slice.SetLayer(_slice.Layer + 1); break;   // ]
            case (VirtualKey)219 when _viewMode != 0: _slice.SetLayer(_slice.Layer - 1); break;   // [
            case VirtualKey.PageUp: StepClip(1); break;
            case VirtualKey.PageDown: StepClip(-1); break;
            case VirtualKey.Home: S.ClipAxis = -1; Notify("已关闭剖切"); break;
            default: return;
        }
        _tools.Sync();
        View.RefreshPreview();
        e.Handled = true;
    }

    private void StepClip(int d)
    {
        if (S.ClipAxis < 0) { S.ClipAxis = 1; S.ClipMin = 0; S.ClipMax = 0; }
        else
        {
            int max = S.Model.Size(S.ClipAxis) - 1;
            S.ClipMax = Math.Clamp(S.ClipMax + d, 0, max);
            if (S.ClipMin > S.ClipMax) S.ClipMin = S.ClipMax;
        }
        Notify($"剖切：显示 {"XYZ"[S.ClipAxis]} = {S.ClipMin} … {S.ClipMax}（Home 关闭）");
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!S.Dirty || S.Model.CountSolid() == 0) return;
        args.Cancel = true;
        if (await ConfirmDiscardAsync()) { S.Dirty = false; Close(); }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        DependencyInstaller.Cancel();
        S.Hy3d.Dispose();
    }
}

/// <summary>最近打开的文件（userdata/recent.txt）。</summary>
public static class RecentFiles
{
    private static string FilePath => Path.Combine(AppPaths.UserData, "recent.txt");

    public static List<string> Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(File.Exists).Distinct().ToList() : new(); }
        catch (Exception) { return new(); }
    }

    public static void Add(string path)
    {
        try
        {
            var list = Load();
            list.Remove(path);
            list.Insert(0, path);
            File.WriteAllLines(FilePath, list.Take(10));
        }
        catch (Exception) { }
    }
}
