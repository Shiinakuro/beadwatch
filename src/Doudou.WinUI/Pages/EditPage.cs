using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Core.Ops;
using Doudou.Core.Recon;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>② 模型编辑：笔刷参数、调色板、剖切、空心化与整体变换。</summary>
public sealed class EditPage : IStepPage
{
    public string Header => "模型编辑";
    public string Description => "像 MagicaVoxel 一样直接在三维模型上贴豆、擦除、上色";
    public UIElement View { get; }

    private readonly IHost _h;
    private Session S => _h.S;
    private readonly ItemsWrap _palette = new() { HSpacing = 4, VSpacing = 4 };
    private readonly ItemsWrap _brandPalette = new() { HSpacing = 3, VSpacing = 3 };
    private readonly TextBox _search = new() { PlaceholderText = "搜索色号，如 A6 / H2" };
    private readonly Border _current = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2) };
    private readonly TextBlock _currentText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _clipAxis;
    private readonly Slider _clipMin = new() { Minimum = 0, StepFrequency = 1 }, _clipMax = new() { Minimum = 0, StepFrequency = 1 };
    private readonly NumberBox _thick = new() { Value = 1, Minimum = 1, Maximum = 6, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly NumberBox _ribs = new() { Value = 0, Minimum = 0, Maximum = 20, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private int _interior;
    private readonly TextBlock _selInfo = Caption("");
    private bool _sync;
    private int _paletteCount = -1;

    public EditPage(IHost h)
    {
        _h = h;
        var root = new StackPanel();

        var sphere = new ToggleSwitch { OnContent = "球形", OffContent = "立方", IsOn = true };
        sphere.Toggled += (_, _) => S.SphereBrush = sphere.IsOn;
        root.Children.Add(Card("笔刷", "左侧工具栏选择动作和笔刷形状；这里调整单格笔刷的大小。",
            SliderRow("半径", 0, 8, 0, v => S.BrushSize = (int)v, 1, "0", " 颗").Row, Row("形状", sphere)));

        var cur = new Grid { ColumnSpacing = 12 };
        cur.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cur.ColumnDefinitions.Add(new ColumnDefinition());
        _current.BorderBrush = Accent;
        cur.Children.Add(_current);
        Grid.SetColumn(_currentText, 1);
        cur.Children.Add(_currentText);
        _search.TextChanged += (_, _) => BuildBrandPalette();
        var brandExp = new Expander
        {
            Header = "品牌色卡（点击加入并选用）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { _search, new ScrollViewer { Content = _brandPalette, MaxHeight = 280 } } },
        };
        root.Children.Add(Card("颜色", "点击选用；右键：全部替换为当前色 / 选中该色。",
            cur, _palette,
            Flow(Btn("任意色", () => { S.CurrentColor = S.Model.AnyColorIndex(new Rgb(64, 74, 70)); RefreshPalette(); }, G.Hide, tip: "藏在内部、看不见的填充豆，可以用任何颜色"),
                 Btn("合并相近色", MergeSimilar, G.Merge, tip: "把 ΔE<6 的相近颜色合并，减少色数"),
                 Btn("映射到色卡", () => Transform("映射到色卡", m => { var c = m.Clone(); Quantizer.RemapToBrand(c, S.Lib, S.Settings.DefaultBrand); return c; }), G.Refresh)),
            brandExp));

        _clipAxis = Combo(new[] { "关闭", "X（左右）", "Y（水平层）", "Z（前后）" }, 0, i =>
        {
            if (_sync) return;
            S.ClipAxis = i - 1;
            UpdateClipRange();
        });
        _clipMin.ValueChanged += (_, e) => { if (_sync) return; S.ClipMin = (int)e.NewValue; if (_clipMax.Value < e.NewValue) _clipMax.Value = e.NewValue; };
        _clipMax.ValueChanged += (_, e) => { if (_sync) return; S.ClipMax = (int)e.NewValue; if (_clipMin.Value > e.NewValue) _clipMin.Value = e.NewValue; };
        root.Children.Add(Card("剖切（逐层编辑）", "只显示并编辑范围内的豆。PageUp / PageDown 逐层移动，Home 关闭。",
            Row("剖切轴", _clipAxis), Row("起始层", _clipMin), Row("结束层", _clipMax)));

        root.Children.Add(Card("空心化", "只保留外壳，内部挖空或标为任意色——真人图纸的通用做法，省豆减重。",
            Row("壁厚", _thick),
            Row("内部", Combo(new[] { "挖空", "改为任意色" }, 0, i => _interior = i)),
            Row("加强筋", _ribs),
            Caption("加强筋：每隔 N 层（水平）在内部加一道十字任意色筋，0 = 不加。"),
            Btn("执行空心化", DoHollow, G.Cube)));

        root.Children.Add(Card("选区", null, _selInfo,
            Flow(Btn("全选", SelectAll, G.Select), Btn("清空选择", () => { S.Selection.Clear(); S.RaiseChanged(); UpdateSel(); }),
                 Btn("删除所选", DeleteSelection, G.Delete), Btn("所选上色", PaintSelection, G.Paint))));

        root.Children.Add(Card("整体变换", null,
            Flow(Btn("旋转 90°", () => Transform("旋转", m => m.RotatedY(1)), G.Rotate),
                 Btn("左右镜像", () => Transform("左右镜像", m => { var c = m.Clone(); c.Mirror(0); return c; }), G.Mirror),
                 Btn("前后镜像", () => Transform("前后镜像", m => { var c = m.Clone(); c.Mirror(2); return c; }), G.Mirror),
                 Btn("裁剪画布", () => Transform("裁剪画布", m => m.Cropped(0)), G.Crop),
                 Btn("扩展画布", () => Transform("扩展画布", m => m.Resized(m.SX + 8, m.SY + 8, m.SZ + 8, new Int3(4, 0, 4))), G.Expand),
                 Btn("导出 .vox", async () => await ((MainWindow)_h).ExportVoxAsync(), G.Download))));

        View = root;
        S.PropertyChanged += (_, _) => SyncCurrent();
        S.ModelReplaced += () => _h.View.DispatcherQueue.TryEnqueue(() => { RefreshPalette(); BuildBrandPalette(); UpdateClipRange(); });
        S.ModelChanged += () => _h.View.DispatcherQueue.TryEnqueue(() => { if (S.Model.Palette.Count != _paletteCount) RefreshPalette(); UpdateSel(); });
        RefreshPalette();
        BuildBrandPalette();
    }

    public void OnEnter()
    {
        RefreshPalette();
        UpdateClipRange();
        UpdateSel();
    }

    private void SyncCurrent()
    {
        var bc = S.CurrentBead;
        _current.Background = Solid(bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb);
        _currentText.Text = $"当前色 {bc}\n{bc.Rgb.Hex}";
        _sync = true;
        if (_clipAxis.SelectedIndex != S.ClipAxis + 1) _clipAxis.SelectedIndex = S.ClipAxis + 1;
        if (S.ClipAxis >= 0) { _clipMin.Value = S.ClipMin; _clipMax.Value = S.ClipMax; }
        _sync = false;
    }

    private void UpdateClipRange()
    {
        _sync = true;
        int max = S.ClipAxis >= 0 ? S.Model.Size(S.ClipAxis) - 1 : 0;
        _clipMin.Maximum = _clipMax.Maximum = Math.Max(1, max);
        if (S.ClipAxis >= 0 && (S.ClipMax > max || S.ClipMax < S.ClipMin)) { S.ClipMin = 0; S.ClipMax = max; }
        _clipMin.Value = S.ClipMin; _clipMax.Value = Math.Min(S.ClipMax, max);
        _sync = false;
    }

    private void UpdateSel() => _selInfo.Text = S.Selection.Count == 0 ? "没有选中的豆。左侧工具栏选「选择」后在模型上点选或框选。" : $"已选中 {S.Selection.Count:N0} 颗";

    private void RefreshPalette()
    {
        _palette.Children.Clear();
        var m = S.Model;
        _paletteCount = m.Palette.Count;
        var counts = new int[m.Palette.Count];
        foreach (var c in m.Colors) counts[c]++;
        for (int i = 1; i < m.Palette.Count; i++)
        {
            if (counts[i] == 0 && i != S.CurrentColor) continue;
            var bc = m.Palette[i];
            ushort idx = (ushort)i;
            var b = new Button { Padding = new Thickness(0), Content = Swatch(bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb, 30, bc.IsAny ? "任" : null), Background = null, BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(b, $"{bc}  {bc.Rgb.Hex}  ×{counts[i]:N0}");
            b.Click += (_, _) => { S.CurrentColor = idx; SyncCurrent(); };
            var fly = new MenuFlyout();
            var use = new MenuFlyoutItem { Text = "选用" }; use.Click += (_, _) => { S.CurrentColor = idx; SyncCurrent(); };
            var rep = new MenuFlyoutItem { Text = "全部替换为当前色" }; rep.Click += (_, _) => ReplaceColor(idx);
            var sel = new MenuFlyoutItem { Text = "选中这种颜色的所有豆" }; sel.Click += (_, _) => { S.Selection.Clear(); for (int k = 0; k < m.Count; k++) if (m.Colors[k] == idx) S.Selection.Add(k); S.RaiseChanged(); UpdateSel(); };
            fly.Items.Add(use); fly.Items.Add(rep); fly.Items.Add(sel);
            b.ContextFlyout = fly;
            _palette.Children.Add(b);
        }
        SyncCurrent();
    }

    private void BuildBrandPalette()
    {
        _brandPalette.Children.Clear();
        var brand = S.Lib.Brands.GetValueOrDefault(S.Model.BrandId) ?? S.Lib.Brands.Values.First();
        var q = _search.Text.Trim();
        foreach (var c in brand.Colors)
        {
            if (q.Length > 0 && !c.Code.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            var cc = c;
            var b = new Button { Padding = new Thickness(0), Content = Swatch(c.Rgb, 26), Background = null, BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(b, $"{brand.Title} {c.Code}  {c.Rgb.Hex}");
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
        _h.Notify($"已把 {m.Palette[from]} 全部替换为 {m.Palette[to]}");
    }

    private void MergeSimilar()
    {
        var m = S.Model;
        var counts = new int[m.Palette.Count];
        foreach (var c in m.Colors) counts[c]++;
        var order = Enumerable.Range(1, m.Palette.Count - 1).Where(i => counts[i] > 0 && !m.Palette[i].IsAny).OrderByDescending(i => counts[i]).ToList();
        var target = new ushort[m.Palette.Count];
        int merged = 0;
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
        S.History.Begin("合并相近色");
        for (int i = 0; i < m.Count; i++) if (target[m.Colors[i]] != 0) S.History.SetColor(i, target[m.Colors[i]]);
        S.History.Commit();
        RefreshPalette();
        _h.Notify(merged > 0 ? $"合并了 {merged} 种相近颜色" : "没有足够接近的颜色可合并");
    }

    private void Transform(string name, Func<VoxelModel, VoxelModel> f)
    {
        S.History.BeginSnapshot(name);
        S.Model.CopyFrom(f(S.Model));
        S.Selection.Clear();
        S.History.Commit();
        RefreshPalette();
        UpdateClipRange();
        _h.View.FrameModel();
    }

    private void SelectAll()
    {
        S.Selection.Clear();
        foreach (var i in S.Model.SolidIndices()) if (S.IsVisible(i)) S.Selection.Add(i);
        S.RaiseChanged();
        UpdateSel();
    }

    public void DeleteSelection()
    {
        if (S.Selection.Count == 0) return;
        S.History.Begin("删除所选");
        foreach (var i in S.Selection) S.History.Set(i, 0, 0);
        S.Selection.Clear();
        S.History.Commit();
        UpdateSel();
    }

    private void PaintSelection()
    {
        if (S.Selection.Count == 0) return;
        S.History.Begin("所选上色");
        foreach (var i in S.Selection) if (S.Model.Colors[i] != 0) S.History.SetColor(i, S.CurrentColor);
        S.History.Commit();
    }

    private void DoHollow()
    {
        var o = new HollowOptions
        {
            Thickness = (int)(double.IsNaN(_thick.Value) ? 1 : _thick.Value),
            Interior = _interior == 1 ? InteriorMode.AnyColor : InteriorMode.Remove,
            RibEvery = (int)(double.IsNaN(_ribs.Value) ? 0 : _ribs.Value),
        };
        int before = S.Model.CountSolid();
        var (removed, any, ribs) = Hollow.Apply(S.Model, S.History, o);
        RefreshPalette();
        _h.Notify($"空心化完成：{before:N0} → {S.Model.CountSolid():N0} 颗（挖空 {removed:N0}，任意色 {any:N0}，加强筋 {ribs:N0}）", InfoBarSeverity.Success);
    }
}
