using Doudou.Core.Color;
using Doudou.Core.Edit;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Controls;

/// <summary>视口里的浮动工具栏（左：动作/笔刷/镜像/当前色）与视角栏（右上）。</summary>
public sealed class ViewportTools
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly Dictionary<EditAction, ToggleButton> _act = new();
    private readonly Dictionary<BrushMode, ToggleButton> _brush = new();
    private readonly ToggleButton _mx = TextToggle("X", "左右镜像（Shift+X）", 24), _my = TextToggle("Y", "上下镜像（Shift+Y）", 24), _mz = TextToggle("Z", "前后镜像（Shift+Z）", 24);
    private readonly CheckBox _bead = new() { Content = "豆孔", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _grid = new() { Content = "描边", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _tint = new() { Content = "按零件着色", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _check = new() { Content = "成品检查", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideSel = new() { Content = "隐藏选中", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _bright = new() { Minimum = 0.5, Maximum = 1.6, Value = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _swatch = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2) };
    private Border? _displayDot;
    private bool _sync;

    public ViewportTools(IHost h)
    {
        _h = h;
        h.S.PropertyChanged += (_, _) => Sync();
    }

    private static Border Floating(UIElement child) => new()
    {
        Child = child,
        Background = Theme.B(Theme.Navy, 0xEE),
        BorderBrush = Theme.B(Theme.Orange, 0x70),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2, 12, 2, 12),
        Padding = new Thickness(6),
    };

    private static TextBlock Section(string t) => new()
    {
        Text = t, FontSize = 11, FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Theme.B(Theme.Orange),
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4), RenderTransform = new Microsoft.UI.Xaml.Media.SkewTransform { AngleX = -10 },
    };

    public FrameworkElement BuildToolbar()
    {
        var sp = new StackPanel();
        sp.Children.Add(Section("动作"));
        foreach (var (a, glyph, label, tip) in new[]
                 {
                     (EditAction.Attach, G.Add, "贴豆", "在点击的面上贴一颗新豆（1）"),
                     (EditAction.Erase, G.Erase, "擦除", "删除豆子（2）"),
                     (EditAction.Paint, G.Paint, "上色", "把豆子改成当前色（3）"),
                     (EditAction.PartPaint, G.Part, "零件", "把豆子分配给当前零件（4）"),
                     (EditAction.Select, G.Select, "选择", "选择豆子：Shift 加选、Ctrl 减选（5）"),
                     (EditAction.Pick, G.Pick, "吸色", "吸取颜色与零件（Alt+左键）"),
                 })
        {
            var t = ToolToggle(glyph, label, tip);
            t.Click += (_, _) => { if (_sync) return; S.Action = a; Sync(); _h.View.RefreshPreview(); };
            _act[a] = t;
            sp.Children.Add(t);
        }
        sp.Children.Add(Section("笔刷"));
        var grid = new Grid { ColumnSpacing = 0, RowSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        int k = 0;
        foreach (var (b, label, tip) in new[]
                 {
                     (BrushMode.Voxel, "单格", "单格 / 球形笔刷，可拖动连续绘制（V）"), (BrushMode.Face, "整面", "同色同平面连着的一整片（G）"),
                     (BrushMode.Box, "矩形", "在点击的平面上拖出矩形（B）"), (BrushMode.Line, "直线", "拖出直线（L）"),
                     (BrushMode.Fill, "填充", "三维同色连通填充（R）"), (BrushMode.Plane, "平面", "同一层里连着的区域（P）"),
                 })
        {
            var t = TextToggle(label, tip, 25);
            t.Width = 25; t.Height = 26; t.FontSize = 10.5; t.Margin = new Thickness(0, 0, k % 2 == 0 ? 2 : 0, 2);
            t.Click += (_, _) => { if (_sync) return; S.Brush = b; Sync(); _h.View.RefreshPreview(); };
            _brush[b] = t;
            Grid.SetColumn(t, k % 2);
            if (grid.RowDefinitions.Count <= k / 2) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(t, k / 2);
            grid.Children.Add(t);
            k++;
        }
        sp.Children.Add(grid);
        sp.Children.Add(Section("镜像"));
        var mirror = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in new[] { _mx, _my, _mz }) { t.Margin = new Thickness(1, 0, 1, 0); mirror.Children.Add(t); }
        _mx.Click += (_, _) => { if (!_sync) S.MirrorX = _mx.IsChecked == true; };
        _my.Click += (_, _) => { if (!_sync) S.MirrorY = _my.IsChecked == true; };
        _mz.Click += (_, _) => { if (!_sync) S.MirrorZ = _mz.IsChecked == true; };
        sp.Children.Add(mirror);
        sp.Children.Add(Section("当前色"));
        _swatch.BorderBrush = Accent;
        _swatch.HorizontalAlignment = HorizontalAlignment.Center;
        var sw = new Button { Content = _swatch, Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };
        sw.Click += (_, _) => _h.GoStep(2);
        sp.Children.Add(sw);
        Sync();
        // 54 宽的工具栏放在可滚动容器里，窗口矮时不会被截断
        return Floating(new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 760 });
    }

    public FrameworkElement BuildViewBar()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        Button V(string label, string tip, Action a)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Background = null, BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => a();
            return b;
        }
        sp.Children.Add(V("正", "正面", () => _h.View.SetView(0, 0)));
        sp.Children.Add(V("侧", "左侧", () => _h.View.SetView(90, 0)));
        sp.Children.Add(V("背", "背面", () => _h.View.SetView(180, 0)));
        sp.Children.Add(V("顶", "俯视", () => _h.View.SetView(0, 89)));
        sp.Children.Add(V("斜", "斜视", () => _h.View.SetView(35, 28)));
        sp.Children.Add(V("居中", "居中显示整个模型（F）", () => _h.View.FrameModel()));
        sp.Children.Add(new AppBarSeparator());

        ToolTipService.SetToolTip(_bead, "显示豆子中间的孔；成品检查时自动隐藏");
        ToolTipService.SetToolTip(_grid, "显示豆格描边");
        ToolTipService.SetToolTip(_tint, "按零件着色（拆件页常用）");
        ToolTipService.SetToolTip(_check, "成品检查：圆柱豆 + 柔光 + 亮背景（C）");
        ToolTipService.SetToolTip(_hideSel, "临时隐藏选中的豆，检查空心内腔（H）");
        _bead.Checked += (_, _) => { if (!_sync) S.BeadLook = true; };
        _bead.Unchecked += (_, _) => { if (!_sync) S.BeadLook = false; };
        _grid.Checked += (_, _) => { if (!_sync) S.ShowGrid = true; };
        _grid.Unchecked += (_, _) => { if (!_sync) S.ShowGrid = false; };
        _tint.Checked += (_, _) => { if (!_sync) S.Display = DisplayMode.PartTint; };
        _tint.Unchecked += (_, _) => { if (!_sync) S.Display = DisplayMode.Color; };
        _check.Checked += (_, _) => { if (!_sync) S.CheckMode = true; };
        _check.Unchecked += (_, _) => { if (!_sync) S.CheckMode = false; };
        _hideSel.Checked += (_, _) => { if (!_sync) S.HideSelection = true; };
        _hideSel.Unchecked += (_, _) => { if (!_sync) S.HideSelection = false; };
        ToolTipService.SetToolTip(_bright, "成品检查亮度（仅成品模式生效）");
        _bright.ValueChanged += (_, _) => { if (!_sync) S.Brightness = (float)_bright.Value; };

        var flyoutBody = new StackPanel { Spacing = Theme.S2, MinWidth = 232 };
        flyoutBody.Children.Add(new TextBlock
        {
            Text = "显示选项", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = Theme.F4, Foreground = Theme.B(Theme.Text1),
        });
        flyoutBody.Children.Add(_bead);
        flyoutBody.Children.Add(_grid);
        flyoutBody.Children.Add(_tint);
        flyoutBody.Children.Add(new Border { Height = 1, Background = Theme.B(Theme.Text3, 0x30), Margin = new Thickness(0, 2, 0, 2) });
        flyoutBody.Children.Add(_check);
        flyoutBody.Children.Add(_hideSel);
        flyoutBody.Children.Add(new TextBlock { Text = "成品亮度", FontSize = Theme.F2, Foreground = Theme.B(Theme.Text2), Margin = new Thickness(0, 6, 0, 0) });
        _bright.Width = double.NaN;
        flyoutBody.Children.Add(_bright);
        var reset = Btn("重置显示", () =>
        {
            S.BeadLook = true;
            S.ShowGrid = true;
            S.CheckMode = false;
            S.HideSelection = false;
            S.Brightness = 1f;
        }, G.Refresh);
        reset.Margin = new Thickness(0, Theme.S1, 0, 0);
        reset.HorizontalAlignment = HorizontalAlignment.Stretch;
        flyoutBody.Children.Add(reset);

        var displayContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var eye = Icon(G.Eye, 14);
        eye.Opacity = 0.8;
        displayContent.Children.Add(eye);
        displayContent.Children.Add(new TextBlock { Text = "显示", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _displayDot = new Border
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.B(Theme.Orange),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        displayContent.Children.Add(_displayDot);
        var display = new Button
        {
            Content = displayContent, Padding = new Thickness(10, 4, 10, 4), FontSize = 12,
            Background = null, BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(display, "显示选项：豆孔、描边、零件色、成品检查");
        AutomationProperties.SetName(display, "显示选项");
        display.Flyout = new Flyout { Content = flyoutBody, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        sp.Children.Add(display);
        return Floating(sp);
    }

    public void Sync()
    {
        _sync = true;
        foreach (var (a, t) in _act) t.IsChecked = S.Action == a;
        foreach (var (b, t) in _brush) t.IsChecked = S.Brush == b;
        _mx.IsChecked = S.MirrorX; _my.IsChecked = S.MirrorY; _mz.IsChecked = S.MirrorZ;
        _bead.IsChecked = S.BeadLook; _grid.IsChecked = S.ShowGrid; _tint.IsChecked = S.Display == DisplayMode.PartTint;
        _check.IsChecked = S.CheckMode; _hideSel.IsChecked = S.HideSelection; _bright.Value = S.Brightness;
        _bright.IsEnabled = S.CheckMode;   // 亮度只在成品检查模式下起作用，平时灰掉免得以为坏了
        if (_displayDot != null)
            _displayDot.Visibility = S.CheckMode || S.HideSelection || S.Display == DisplayMode.PartTint ? Visibility.Visible : Visibility.Collapsed;
        var bc = S.CurrentBead;
        _swatch.Background = Solid(bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb);
        ToolTipService.SetToolTip(_swatch, $"当前色：{bc}  {bc.Rgb.Hex}");
        _sync = false;
    }
}
