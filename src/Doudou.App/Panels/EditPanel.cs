using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Doudou.Core.Color;
using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Core.Ops;
using Doudou.Core.Recon;

using Doudou.Editor;
namespace Doudou.App.Panels;

/// <summary>② 模型编辑（MagicaVoxel 式工具 + 调色板 + 整体操作）。</summary>
public sealed class EditPanel : UserControl
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly Dictionary<EditAction, ToggleButton> _actions = new();
    private readonly Dictionary<BrushMode, ToggleButton> _brushes = new();
    private readonly ToggleButton _mx = Ui.Toggle("X", "X 轴镜像（Shift+X）"), _my = Ui.Toggle("Y", "Y 轴镜像"), _mz = Ui.Toggle("Z", "Z 轴镜像");
    private readonly WrapPanel _modelPalette = new();
    private readonly WrapPanel _brandPalette = new();
    private readonly Border _current = new() { Width = 46, Height = 46, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _currentText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Slider _clipMin = new() { Minimum = 0 }, _clipMax = new() { Minimum = 0 };
    private readonly ComboBox _clipAxis;
    private bool _sync;

    public EditPanel(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        root.Children.Add(Ui.H1("模型编辑"));

        root.Children.Add(Ui.H2("动作"));
        var acts = Ui.Wrap();
        foreach (var (a, name, key) in new[] { (EditAction.Attach, "贴豆", "1"), (EditAction.Erase, "擦除", "2"), (EditAction.Paint, "上色", "3"), (EditAction.Select, "选择", "5"), (EditAction.Pick, "吸色", "Alt") })
        {
            var t = Ui.Toggle(name, $"{name}（{key}）");
            t.Click += (_, _) => { S.Action = a; Sync(); };
            _actions[a] = t; acts.Children.Add(t);
        }
        root.Children.Add(acts);

        root.Children.Add(Ui.H2("笔刷"));
        var brs = Ui.Wrap();
        foreach (var (b, name, tip) in new[]
                 {
                     (BrushMode.Voxel, "单格", "V：单颗/球形笔刷，可拖动连续绘制"), (BrushMode.Face, "整面", "G：同色同平面连续的一整片"),
                     (BrushMode.Box, "矩形", "B：在点击的平面上拖出矩形"), (BrushMode.Line, "直线", "L：拖出直线"),
                     (BrushMode.Fill, "填充", "R：三维同色连通填充"), (BrushMode.Plane, "平面", "P：同一层的连通区域"),
                 })
        {
            var t = Ui.Toggle(name, tip);
            t.Click += (_, _) => { S.Brush = b; Sync(); };
            _brushes[b] = t; brs.Children.Add(t);
        }
        root.Children.Add(brs);
        root.Children.Add(Ui.SliderRow("笔刷半径", 0, 8, 0, v => S.BrushSize = (int)v).Row);
        var sphere = new CheckBox { Content = "球形笔刷（否则立方）", IsChecked = true };
        sphere.Click += (_, _) => S.SphereBrush = sphere.IsChecked == true;
        root.Children.Add(sphere);
        var mirror = Ui.Wrap(new TextBlock { Text = "镜像", Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _mx, _my, _mz);
        _mx.Click += (_, _) => S.MirrorX = _mx.IsChecked == true;
        _my.Click += (_, _) => S.MirrorY = _my.IsChecked == true;
        _mz.Click += (_, _) => S.MirrorZ = _mz.IsChecked == true;
        root.Children.Add(mirror);

        root.Children.Add(Ui.H2("颜色"));
        var cur = new DockPanel();
        DockPanel.SetDock(_current, Dock.Left);
        cur.Children.Add(_current); cur.Children.Add(_currentText);
        root.Children.Add(cur);
        root.Children.Add(Ui.Hint("模型中正在使用的颜色（点击选用，右键：把模型里这种颜色全部替换为当前色）"));
        root.Children.Add(_modelPalette);
        root.Children.Add(Ui.Wrap(
            Ui.Btn("任意色", (_, _) => { S.CurrentColor = S.Model.AnyColorIndex(new Rgb(64, 74, 70)); RefreshPalette(); }, "藏在内部、看不见的豆，可用任何颜色"),
            Ui.Btn("合并相近色", (_, _) => MergeSimilar(), "把 ΔE<6 的相近颜色合并，减少色数"),
            Ui.Btn("映射到色卡", (_, _) => Remap(), "把所有颜色重新匹配到默认品牌色卡")));
        var brandExp = new Expander { Header = new TextBlock { Text = "品牌色卡（点击加入并选用）" }, Content = new ScrollViewer { Content = _brandPalette, MaxHeight = 260 }, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(brandExp);

        root.Children.Add(Ui.H2("剖切（逐层编辑）"));
        _clipAxis = Ui.Combo(new[] { "关闭", "X 轴", "Y 轴（水平层）", "Z 轴" }, 0, i =>
        {
            if (_sync) return;
            S.ClipAxis = i - 1;
            UpdateClipRange();
        });
        root.Children.Add(Ui.Row("剖切轴", _clipAxis));
        _clipMin.ValueChanged += (_, e) => { if (!_sync) { S.ClipMin = (int)e.NewValue; if (_clipMax.Value < e.NewValue) _clipMax.Value = e.NewValue; } };
        _clipMax.ValueChanged += (_, e) => { if (!_sync) { S.ClipMax = (int)e.NewValue; if (_clipMin.Value > e.NewValue) _clipMin.Value = e.NewValue; } };
        _clipMin.IsSnapToTickEnabled = _clipMax.IsSnapToTickEnabled = true;
        _clipMin.TickFrequency = _clipMax.TickFrequency = 1;
        root.Children.Add(Ui.Row("起始层", _clipMin));
        root.Children.Add(Ui.Row("结束层", _clipMax));
        root.Children.Add(Ui.Hint("PageUp / PageDown 逐层移动；只显示范围内的豆，编辑只作用于可见豆。"));

        root.Children.Add(Ui.H2("整体操作"));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("空心化…", (_, _) => HollowDialog(), "保留外壳，内部挖空或标为任意色，省豆减重"),
            Ui.Btn("删除所选", (_, _) => DeleteSelection()),
            Ui.Btn("所选上色", (_, _) => PaintSelection()),
            Ui.Btn("全选", (_, _) => { S.Selection.Clear(); foreach (var i in S.Model.SolidIndices()) S.Selection.Add(i); S.RaiseChanged(); })));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("旋转 90°", (_, _) => Transform("旋转", m => m.RotatedY(1))),
            Ui.Btn("左右镜像", (_, _) => Transform("左右镜像", m => { var c = m.Clone(); c.Mirror(0); return c; })),
            Ui.Btn("前后镜像", (_, _) => Transform("前后镜像", m => { var c = m.Clone(); c.Mirror(2); return c; })),
            Ui.Btn("裁剪画布", (_, _) => Transform("裁剪画布", m => m.Cropped(0))),
            Ui.Btn("扩展画布", (_, _) => Transform("扩展画布", m => m.Resized(m.SX + 8, m.SY + 8, m.SZ + 8, new Int3(4, 0, 4))))));

        root.Children.Add(Ui.H2("显示"));
        var bead = new CheckBox { Content = "豆子孔洞效果", IsChecked = true };
        bead.Click += (_, _) => S.BeadLook = bead.IsChecked == true;
        var grid = new CheckBox { Content = "豆格描边", IsChecked = true };
        grid.Click += (_, _) => S.ShowGrid = grid.IsChecked == true;
        root.Children.Add(bead); root.Children.Add(grid);

        Content = Ui.Page(root);
        S.PropertyChanged += (_, _) => Sync();
        S.ModelReplaced += () => { RefreshPalette(); UpdateClipRange(); };
        S.ModelChanged += () => Dispatcher.BeginInvoke(RefreshPaletteIfChanged);
        BuildBrandPalette();
        RefreshPalette();
        UpdateClipRange();
        Sync();
    }

    private void Sync()
    {
        _sync = true;
        foreach (var (a, t) in _actions) t.IsChecked = S.Action == a;
        foreach (var (b, t) in _brushes) t.IsChecked = S.Brush == b;
        _mx.IsChecked = S.MirrorX; _my.IsChecked = S.MirrorY; _mz.IsChecked = S.MirrorZ;
        var bc = S.CurrentBead;
        _current.Background = Ui.Brush(bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb);
        _current.BorderBrush = Ui.Res("Accent");
        _currentText.Text = $"当前色：{bc}\n{bc.Rgb.Hex}";
        if (_clipAxis.SelectedIndex != S.ClipAxis + 1) _clipAxis.SelectedIndex = S.ClipAxis + 1;
        if (S.ClipAxis >= 0) { _clipMin.Value = S.ClipMin; _clipMax.Value = S.ClipMax; }
        _sync = false;
    }

    private void UpdateClipRange()
    {
        _sync = true;
        int max = S.ClipAxis >= 0 ? S.Model.Size(S.ClipAxis) - 1 : 0;
        _clipMin.Maximum = _clipMax.Maximum = max;
        if (S.ClipAxis >= 0 && S.ClipMax > max) { S.ClipMin = 0; S.ClipMax = max; }
        _clipMin.Value = S.ClipMin; _clipMax.Value = Math.Min(S.ClipMax, max);
        _sync = false;
    }

    private int _paletteCount = -1;
    private void RefreshPaletteIfChanged()
    {
        if (S.Model.Palette.Count != _paletteCount) RefreshPalette();
    }

    private void RefreshPalette()
    {
        _modelPalette.Children.Clear();
        var m = S.Model;
        _paletteCount = m.Palette.Count;
        var counts = new int[m.Palette.Count];
        foreach (var c in m.Colors) counts[c]++;
        for (int i = 1; i < m.Palette.Count; i++)
        {
            var bc = m.Palette[i];
            ushort idx = (ushort)i;
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(0),
                Background = Ui.Brush(bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb),
                ToolTip = $"{bc}  {bc.Rgb.Hex}  ×{counts[i]}",
                Content = new TextBlock { Text = bc.IsAny ? "任" : "", FontSize = 10, Foreground = System.Windows.Media.Brushes.White },
            };
            b.Click += (_, _) => { S.CurrentColor = idx; Sync(); };
            b.MouseRightButtonUp += (_, _) => ReplaceColor(idx);
            _modelPalette.Children.Add(b);
        }
        Sync();
    }

    private void BuildBrandPalette()
    {
        _brandPalette.Children.Clear();
        var brand = S.Lib.Brands.GetValueOrDefault(S.Model.BrandId) ?? S.Lib.Brands.Values.First();
        foreach (var c in brand.Colors)
        {
            var cc = c;
            var b = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 2, 2), Padding = new Thickness(0),
                Background = Ui.Brush(c.Rgb), ToolTip = $"{brand.Title} {c.Code} {c.Rgb.Hex}",
            };
            b.Click += (_, _) => { S.UseBrandColor(cc); RefreshPalette(); };
            _brandPalette.Children.Add(b);
        }
    }

    private void ReplaceColor(ushort from)
    {
        var to = S.CurrentColor;
        if (from == to || to == 0) return;
        var m = S.Model;
        S.History.Begin($"替换颜色 {m.Palette[from]} → {m.Palette[to]}");
        for (int i = 0; i < m.Count; i++) if (m.Colors[i] == from) S.History.SetColor(i, to);
        S.History.Commit();
        _h.Say($"已把 {m.Palette[from]} 全部替换为 {m.Palette[to]}");
    }

    private void MergeSimilar()
    {
        var m = S.Model;
        var counts = new int[m.Palette.Count];
        foreach (var c in m.Colors) counts[c]++;
        S.History.Begin("合并相近色");
        int merged = 0;
        var order = Enumerable.Range(1, m.Palette.Count - 1).Where(i => counts[i] > 0 && !m.Palette[i].IsAny).OrderByDescending(i => counts[i]).ToList();
        var target = new ushort[m.Palette.Count];
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            if (target[i] != 0) continue;
            for (int j = k + 1; j < order.Count; j++)
            {
                int o = order[j];
                if (target[o] == 0 && ColorMath.DeltaE(m.Palette[i].Rgb, m.Palette[o].Rgb) < 6) { target[o] = (ushort)i; merged++; }
            }
        }
        for (int i = 0; i < m.Count; i++) if (target[m.Colors[i]] != 0) S.History.SetColor(i, target[m.Colors[i]]);
        S.History.Commit();
        _h.Say($"合并了 {merged} 种相近颜色");
    }

    private void Remap() => Transform("映射到色卡", m => { var c = m.Clone(); Quantizer.RemapToBrand(c, S.Lib, S.Settings.DefaultBrand); return c; });

    private void Transform(string name, Func<Doudou.Core.Model.VoxelModel, Doudou.Core.Model.VoxelModel> f)
    {
        S.History.BeginSnapshot(name);
        var r = f(S.Model);
        S.Model.CopyFrom(r);
        S.Selection.Clear();
        S.History.Commit();
        RefreshPalette();
        UpdateClipRange();
        _h.View.FrameModel();
    }

    public void DeleteSelection()
    {
        if (S.Selection.Count == 0) return;
        S.History.Begin("删除所选");
        foreach (var i in S.Selection) S.History.Set(i, 0, 0);
        S.Selection.Clear();
        S.History.Commit();
    }

    private void PaintSelection()
    {
        if (S.Selection.Count == 0) return;
        S.History.Begin("所选上色");
        foreach (var i in S.Selection) if (S.Model.Colors[i] != 0) S.History.SetColor(i, S.CurrentColor);
        S.History.Commit();
    }

    private void HollowDialog()
    {
        var t = Prompt.Ask(Window.GetWindow(this)!, "空心化", "外壳厚度（颗，1~3）。内部将被挖空；输入如「1 任意色」则内部改为任意色而不删除；「1 筋4」每 4 层加一道十字筋", "1");
        if (t == null) return;
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(parts[0], out int th) || th < 1 || th > 6) return;
        var o = new HollowOptions { Thickness = th };
        foreach (var p in parts.Skip(1))
        {
            if (p.Contains("任意")) o.Interior = InteriorMode.AnyColor;
            if (p.StartsWith("筋") && int.TryParse(p[1..], out int every)) o.RibEvery = every;
        }
        int before = S.Model.CountSolid();
        var (removed, any, ribs) = Hollow.Apply(S.Model, S.History, o);
        RefreshPalette();
        _h.Say($"空心化：{before} → {S.Model.CountSolid()} 颗（挖空 {removed}，任意色 {any}，加强筋 {ribs}）");
    }
}
