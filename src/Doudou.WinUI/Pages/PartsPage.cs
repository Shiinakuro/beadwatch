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
    public UIElement? Footer { get; }

    private readonly IHost _h;
    private Session S => _h.S;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, MaxHeight = 360 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recommend = Caption("");
    private readonly ContentControl _empty = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ModelPartsReport? _rep;
    private bool _refreshing, _active;
    private int _tooth = 2;
    private ToggleSwitch _tintToggle = null!;
    private bool _syncTint;

    public PartsPage(IHost h)
    {
        _h = h;
        var root = new StackPanel();

        root.Children.Add(Card("一键初稿", null, _recommend,
            Flow(Btn("水平分层", () => Do("水平分层", () => SplitTools.LayerSplit(S.Model, S.History, 1)), G.Layers, tip: "沿高度切成一圈圈环片（人物、动物、铃铛）"),
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

        _tintToggle = new ToggleSwitch { OnContent = "按零件着色", OffContent = "按颜色显示", IsOn = false };
        _tintToggle.Toggled += (_, _) => { if (!_syncTint) S.Display = _tintToggle.IsOn ? DisplayMode.PartTint : DisplayMode.Color; };
        root.Children.Add(Card("预览", "开启「按零件着色」时，红色斜纹 = 还没有分到零件的豆；零件很多时建议按颜色显示。",
            SliderRow("爆炸", 1, 3, 1, v => S.Explode = (float)v, 0.05, "0.00", "×").Row, _tintToggle));

        View = root;
        var footer = new StackPanel { Spacing = 2 };
        var auto = WideBtn("智能推荐拆分", Auto, G.Magic);
        auto.Margin = new Thickness(0);
        footer.Children.Add(auto);
        footer.Children.Add(NextLink("下一步：检查 →", () => _h.GoStep(4)));
        Footer = footer;
        // 只跟「内容」变化：显示开关（亮度、描边、剖切…）不该让整份零件分析重算一遍
        S.ContentChanged += () => { if (_active) _h.View.DispatcherQueue.TryEnqueue(Refresh); };
        S.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Session.Display)) _h.View.DispatcherQueue.TryEnqueue(SyncTint); };
    }

    public void OnEnter()
    {
        _active = true;
        int parts = S.Model.PartList.Count;
        S.Display = parts is > 0 and <= 30 ? DisplayMode.PartTint : DisplayMode.Color;
        SyncTint();
        if (S.Action is EditAction.Attach or EditAction.Erase or EditAction.Paint) { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Plane; }
        var (how, flat) = SplitTools.Recommend(S.Model);
        var estimate = how == "panel" ? "预计 6~12 个板件" : $"预计 {S.Model.SY}~{S.Model.SY * 2} 个层板";
        _recommend.Text = $"表面平面度 {flat:P0}，推荐「{(how == "panel" ? "面板拆分（箱体 + 齿口）" : "水平分层（环片）")}」，{estimate}。初稿之后再用零件刷手工调整。" +
                          (parts > 30 ? $"\n当前 {parts} 个零件，已切回按颜色显示；零件多时建议降低豆高、合并碎件或改用面板拆分。" : "");
        Refresh();
    }

    public void OnLeave() => _active = false;

    private void SyncTint()
    {
        if (_tintToggle == null) return;
        _syncTint = true;
        _tintToggle.IsOn = S.Display == DisplayMode.PartTint;
        _syncTint = false;
    }

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
        f();
        S.RaiseChanged();
        int count = S.Model.PartList.Count;
        if (count > 30)
        {
            S.Display = DisplayMode.Color;
            SyncTint();
        }
        else if (count > 0)
        {
            S.Display = DisplayMode.PartTint;
            SyncTint();
        }
        Refresh();
        var msg = $"{name}完成：当前 {count} 个零件";
        if (count > 30) msg += "。零件较多，建议降低豆高、合并碎件或改用面板拆分。";
        _h.Notify(msg, count > 30 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
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
            _empty.Content = none ? Empty(G.Cut, S.Model.CountSolid() == 0 ? "模型还是空的。先导入图片或模型，也可以在空白画布中搭建。" : "还没有零件。先点上面的「智能推荐」生成初稿，再用零件刷微调。",
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

    private void ToggleVisible()
    {
        var sel = SelectedParts();
        if (sel.Count == 0) return;
        S.History.Begin("隐藏/显示零件");
        foreach (var p in sel) p.Visible = !p.Visible;
        S.History.Commit();
        Refresh();
    }

    private void Solo()
    {
        var ids = SelectedIds().ToHashSet();
        if (ids.Count == 0) return;
        S.History.Begin("只看所选零件");
        foreach (var p in S.Model.PartList) p.Visible = ids.Contains(p.Id);
        S.History.Commit();
        Refresh();
    }

    private void ShowAll()
    {
        S.History.Begin("显示全部零件");
        foreach (var p in S.Model.PartList) p.Visible = true;
        S.History.Commit();
        Refresh();
    }

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
