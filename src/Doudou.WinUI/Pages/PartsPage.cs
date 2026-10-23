using Doudou.Core.Edit;
using Doudou.Core.Model;
using Doudou.Core.Parts;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>③ 零件拆分：手工为主（零件刷 + 平面填充），一键初稿为辅。</summary>
public sealed class PartsPage : IStepPage
{
    public string Header => "零件拆分";
    public string Description => "把模型拆成一块块可以单独熨烫的平板零件";
    public UIElement View { get; }

    private readonly IHost _h;
    private Session S => _h.S;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, MaxHeight = 360 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recommend = Caption("");
    private readonly ContentControl _empty = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ModelPartsReport? _rep;
    private bool _refreshing, _active;
    private int _tooth = 2;

    public PartsPage(IHost h)
    {
        _h = h;
        var root = new StackPanel();

        root.Children.Add(Card("一键初稿", null, _recommend,
            Flow(Btn("智能推荐", Auto, G.Magic, accent: true, tip: "按形状自动选择：圆润造型 → 水平环片；方正造型 → 箱体面板 + 齿口"),
                 Btn("水平分层", () => Do("水平分层", () => SplitTools.LayerSplit(S.Model, S.History, 1)), G.Layers, tip: "沿高度切成一圈圈环片（人物、动物、铃铛）"),
                 Btn("面板拆分", () => Do("面板拆分", () => SplitTools.PanelSplit(S.Model, S.History)), G.Cube, tip: "按表面朝向拆成前/后/侧/顶板（建筑、方块造型）"),
                 Btn("竖切 X", () => Do("竖切X", () => SplitTools.LayerSplit(S.Model, S.History, 0))),
                 Btn("竖切 Z", () => Do("竖切Z", () => SplitTools.LayerSplit(S.Model, S.History, 2))),
                 Btn("所选按轴切…", async () => await SliceSelection(), tip: "只对当前选区按指定方向切"))));

        root.Children.Add(Card("整理", null,
            Row("齿口规格", Combo(new[] { "点1空1（小模型咬得紧）", "点2空2（通用）", "点3空3（大模型不易断）" }, 1, i => _tooth = i + 1)),
            Caption("齿口 = 垂直相接板之间的锯齿卡扣。模型小用点1空1；模型大、卡扣要超过 3 颗时用点2空2 或点3空3，避免 1 颗 1 排的脆边（熨烫和拼接时易掉豆断开）。"),
            Flow(Btn("合并碎件", () => Do("合并碎件", () => SplitTools.MergeTiny(S.Model, S.History)), G.Merge),
                 Btn("碎件换向", () => Do("碎件换向", () => SplitTools.ResliceSmall(S.Model, S.History)), G.Rotate, tip: "挂环、手指等细小结构换个方向重切，减少单颗碎件"),
                 Btn("自动齿口", () => Do("齿口", () => SplitTools.FingerJointAll(S.Model, S.History, _tooth)), G.Link, tip: "所有垂直相接的板之间做锯齿卡扣"),
                 Btn("重新编号", () => Do("编号", () => { SplitTools.Renumber(S.Model, S.History); return S.Model.PartList.Count; }), G.Rename),
                 Btn("清空全部", async () => await ClearAll(), G.Delete))));

        var same = new ToggleSwitch { OnContent = "只扩散到同一零件内（把大板切小）", OffContent = "扩散到整层相连的豆", IsOn = true };
        same.Toggled += (_, _) => S.PlaneSamePart = same.IsOn;
        root.Children.Add(Card("手工拆分（零件刷）", "选中列表里的零件（或新建一个），再在模型上点击：平面笔刷会把点中豆所在层的连通区域分给它。",
            Flow(Btn("零件刷 · 平面", () => Brush(BrushMode.Plane), G.Part),
                 Btn("单格", () => Brush(BrushMode.Voxel)), Btn("整面", () => Brush(BrushMode.Face)), Btn("矩形", () => Brush(BrushMode.Box)),
                 Btn("新建零件", NewPart, G.Add), Btn("选区 → 新零件", SelectionToPart, G.Select)),
            Row("平面方向", Combo(new[] { "跟随点击的面", "X（侧板）", "Y（层板）", "Z（前后板）" }, 0, i => S.PlaneAxis = i - 1)),
            same));

        _list.SelectionChanged += (_, _) => OnSelect();
        root.Children.Add(Card("零件列表", null, _summary, _empty, _list,
            Flow(Btn("重命名", async () => await Rename(), G.Rename), Btn("翻面标记", ToggleFlip, G.Flip, tip: "图纸上标「翻」：熨烫后翻面使用"),
                 Btn("隐藏/显示", ToggleVisible, G.Eye), Btn("只看所选", Solo, G.Zoom), Btn("全部显示", ShowAll),
                 Btn("两件齿口", JointSelected, G.Link, tip: "按住 Ctrl 选两块垂直相接的板"), Btn("合并所选", MergeSelected, G.Merge), Btn("取消分配", UnassignSelected, G.Delete))));

        var tint = new ToggleSwitch { OnContent = "按零件着色", OffContent = "按颜色显示", IsOn = true };
        tint.Toggled += (_, _) => S.Display = tint.IsOn ? DisplayMode.PartTint : DisplayMode.Color;
        root.Children.Add(Card("预览", "红色斜纹 = 还没有分到零件的豆。", SliderRow("爆炸", 1, 3, 1, v => S.Explode = (float)v, 0.05, "0.00", "×").Row, tint));

        View = root;
        // 只跟「内容」变化：显示开关（亮度、描边、剖切…）不该让整份零件分析重算一遍
        S.ContentChanged += () => { if (_active) _h.View.DispatcherQueue.TryEnqueue(Refresh); };
    }

    public void OnEnter()
    {
        _active = true;
        S.Display = DisplayMode.PartTint;
        if (S.Action is EditAction.Attach or EditAction.Erase or EditAction.Paint) { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Plane; }
        var (how, flat) = SplitTools.Recommend(S.Model);
        _recommend.Text = $"表面平面度 {flat:P0}，推荐「{(how == "panel" ? "面板拆分（箱体 + 齿口）" : "水平分层（环片）")}」。初稿之后再用零件刷手工调整。";
        Refresh();
    }

    public void OnLeave() => _active = false;

    private void Brush(BrushMode b)
    {
        S.Action = EditAction.PartPaint;
        S.Brush = b;
        _h.View.RefreshPreview();
    }

    private void Auto()
    {
        var (how, _) = SplitTools.Recommend(S.Model);
        Do("智能拆分", () =>
        {
            int n = how == "panel" ? SplitTools.PanelSplit(S.Model, S.History) : SplitTools.LayerSplit(S.Model, S.History, 1);
            if (how == "panel") SplitTools.FingerJointAll(S.Model, S.History, _tooth);
            return n;
        });
    }

    private void Do(string name, Func<int> f)
    {
        if (S.Model.CountSolid() == 0) { _h.Notify("模型是空的", InfoBarSeverity.Warning); return; }
        int r = f();
        S.RaiseChanged();
        Refresh();
        _h.Notify($"{name}完成：当前 {S.Model.PartList.Count} 个零件", InfoBarSeverity.Success);
    }

    private async Task ClearAll()
    {
        if (!await _h.ConfirmAsync("清空全部零件？", "所有豆都会变成「未分配」（可撤销）。", "清空")) return;
        S.History.Begin("清空零件");
        for (int i = 0; i < S.Model.Count; i++) if (S.Model.Colors[i] != 0) S.History.SetPart(i, 0);
        S.Model.PartList.Clear();
        S.History.Commit();
        S.CurrentPart = 0;
    }

    private void NewPart()
    {
        S.History.Begin("新建零件");
        var p = S.Model.NewPart();
        S.History.Commit();
        S.CurrentPart = p.Id;
        Brush(BrushMode.Plane);
        _h.Notify($"已新建 {p.Name}，在模型上点击要分给它的豆");
    }

    private async Task SliceSelection()
    {
        if (S.Selection.Count == 0) { _h.Notify("先用左侧工具栏的「选择」选中一块区域", InfoBarSeverity.Warning); return; }
        var t = await _h.PromptAsync("所选按轴切", "切割方向：X / Y / Z", "Y");
        if (t == null) return;
        int axis = t.Trim().ToUpperInvariant() switch { "X" => 0, "Z" => 2, _ => 1 };
        Do("选区切分", () => SplitTools.SliceByAxis(S.Model, S.History, axis, S.Selection.ToList()));
    }

    private void SelectionToPart()
    {
        if (S.Selection.Count == 0) { _h.Notify("先选中一些豆", InfoBarSeverity.Warning); return; }
        S.History.Begin("选区 → 新零件");
        var p = S.Model.NewPart();
        foreach (var i in S.Selection) S.History.SetPart(i, (ushort)p.Id);
        S.Model.PruneEmptyParts();
        S.History.Commit();
        S.CurrentPart = p.Id;
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _rep = PartAnalyzer.Analyze(S.Model);
            bool none = _rep.Parts.Count == 0;
            _empty.Content = none ? Empty(G.Cut, S.Model.CountSolid() == 0 ? "模型还是空的。先导入图片或在「建造」里搭一个模型。" : "还没有零件。先点上面的「智能推荐」生成初稿，再用零件刷微调。",
                S.Model.CountSolid() == 0 ? null : "智能推荐", S.Model.CountSolid() == 0 ? null : Auto) : null;
            _list.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            int errors = _rep.Parts.Count(p => p.Issues.Any(i => i.Level == IssueLevel.Error));
            int warns = _rep.Parts.Count(p => p.Issues.Any(i => i.Level == IssueLevel.Warning));
            _summary.Text = $"{_rep.Parts.Count} 个零件 · 已分配 {_rep.AssignedBeads:N0}/{_rep.TotalBeads:N0} 颗" +
                            (_rep.Unassigned.Count > 0 ? $" · 未分配 {_rep.Unassigned.Count:N0}" : "") +
                            (errors > 0 ? $" · {errors} 个不可熨烫" : "") + (warns > 0 ? $" · {warns} 个需注意" : "");
            var selected = _list.SelectedItems.OfType<ListViewItem>().Select(i => (int)i.Tag).ToHashSet();
            _list.Items.Clear();
            foreach (var r in _rep.Parts)
            {
                var info = S.Model.FindPart(r.Id);
                if (info == null) continue;
                string status = r.Issues.Any(i => i.Level == IssueLevel.Error) ? G.Warning : r.Issues.Any(i => i.Level == IssueLevel.Warning) ? "!" : "";
                var g = new Grid { ColumnSpacing = 10 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(Swatch(info.Tint, 16));
                var name = new StackPanel { Opacity = info.Visible ? 1 : 0.45 };
                name.Children.Add(new TextBlock { Text = info.Name + (info.Flip ? "  · 翻" : "") + (info.Visible ? "" : "  · 隐藏") });
                name.Children.Add(new TextBlock { Text = $"{SplitTools.AxisChinese(r.PlaneAxis)} · {r.SheetSize.Cols}×{r.SheetSize.Rows} · {r.Beads} 颗", FontSize = 11.5, Opacity = 0.65 });
                Grid.SetColumn(name, 1);
                g.Children.Add(name);
                if (status != "")
                {
                    var st = status == "!" ? (UIElement)new TextBlock { Text = "注意", FontSize = 11, Foreground = Solid(0xF5, 0xB5, 0x42) } : Icon(G.Warning, 14);
                    Grid.SetColumn((FrameworkElement)st, 2);
                    g.Children.Add(st);
                }
                var item = new ListViewItem { Content = g, Tag = info.Id, Padding = new Thickness(8, 4, 8, 4) };
                if (r.Issues.Count > 0) ToolTipService.SetToolTip(item, string.Join("\n", r.Issues.Select(i => i.Message)));
                _list.Items.Add(item);
                if (selected.Contains(info.Id) || (selected.Count == 0 && info.Id == S.CurrentPart)) item.IsSelected = true;
            }
        }
        finally { _refreshing = false; }
    }

    private List<int> SelectedIds() => _list.SelectedItems.OfType<ListViewItem>().Select(i => (int)i.Tag).ToList();
    private List<PartInfo> SelectedParts() => SelectedIds().Select(id => S.Model.FindPart(id)).OfType<PartInfo>().ToList();

    private void OnSelect()
    {
        if (_refreshing) return;
        var ids = SelectedIds();
        if (ids.Count == 0) return;
        S.CurrentPart = ids[0];
        S.RaiseChanged();
    }

    private async Task Rename()
    {
        var sel = SelectedParts();
        if (sel.Count != 1) { _h.Notify("请选择一个零件", InfoBarSeverity.Warning); return; }
        var n = await _h.PromptAsync("重命名零件", "名称", sel[0].Name);
        if (string.IsNullOrWhiteSpace(n)) return;
        S.History.Begin("重命名零件");
        sel[0].Name = n.Trim();
        S.History.Commit();
    }

    private void ToggleFlip()
    {
        var sel = SelectedParts();
        if (sel.Count == 0) return;
        S.History.Begin("翻面标记");
        foreach (var p in sel) p.Flip = !p.Flip;
        S.History.Commit();
    }

    private void ToggleVisible() { foreach (var p in SelectedParts()) p.Visible = !p.Visible; S.RaiseChanged(); Refresh(); }

    private void Solo()
    {
        var ids = SelectedIds().ToHashSet();
        if (ids.Count == 0) return;
        foreach (var p in S.Model.PartList) p.Visible = ids.Contains(p.Id);
        S.RaiseChanged();
        Refresh();
    }

    private void ShowAll() { foreach (var p in S.Model.PartList) p.Visible = true; S.RaiseChanged(); Refresh(); }

    private void JointSelected()
    {
        var ids = SelectedIds();
        if (ids.Count != 2) { _h.Notify("请按住 Ctrl 在列表中选择两块零件", InfoBarSeverity.Warning); return; }
        int n = SplitTools.FingerJoint(S.Model, S.History, ids[0], ids[1], _tooth);
        _h.Notify(n > 0 ? $"齿口完成：调整了 {n} 颗" : "这两块不是垂直相接的平板，或齿口会把零件切断，未修改", n > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void MergeSelected()
    {
        var ids = SelectedIds();
        if (ids.Count < 2 || _rep == null) return;
        S.History.Begin("合并零件");
        foreach (var r in _rep.Parts.Where(p => ids.Skip(1).Contains(p.Id)))
            foreach (var i in r.Cells) S.History.SetPart(i, (ushort)ids[0]);
        S.Model.PruneEmptyParts();
        S.History.Commit();
    }

    private void UnassignSelected()
    {
        var ids = SelectedIds();
        if (ids.Count == 0 || _rep == null) return;
        S.History.Begin("取消分配");
        foreach (var r in _rep.Parts.Where(p => ids.Contains(p.Id)))
            foreach (var i in r.Cells) S.History.SetPart(i, 0);
        S.Model.PruneEmptyParts();
        S.History.Commit();
    }
}
