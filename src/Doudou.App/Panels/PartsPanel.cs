using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Core.Parts;

using Doudou.Editor;
namespace Doudou.App.Panels;

/// <summary>③ 零件拆分：以手工为主（零件刷 + 平面填充），辅以一键分层/面板/齿口。</summary>
public sealed class PartsPanel : UserControl
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly ListBox _list = new() { Height = 300, SelectionMode = SelectionMode.Extended };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _recommend = Ui.Hint("");
    private ModelPartsReport? _rep;
    private bool _refreshing;

    public PartsPanel(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        root.Children.Add(Ui.H1("零件拆分"));
        root.Children.Add(Ui.Hint("每个零件必须是一块单层平板（可单独熨烫）。先用「一键初稿」快速拆，再用零件刷手工调整边界；零件色显示时，红色斜纹 = 尚未分配零件的豆。"));

        root.Children.Add(Ui.H2("一键初稿"));
        root.Children.Add(_recommend);
        root.Children.Add(Ui.Wrap(
            Ui.Btn("智能推荐", (_, _) => Auto(), "根据形状自动选择分层环片或箱体面板拆法", primary: true),
            Ui.Btn("水平分层", (_, _) => Do("分层", () => SplitTools.LayerSplit(S.Model, S.History, 1)), "沿 Y 轴切成一圈圈环片（人物、动物、铃铛）"),
            Ui.Btn("面板拆分", (_, _) => Do("面板", () => SplitTools.PanelSplit(S.Model, S.History)), "按表面朝向拆成前/后/侧/顶板（建筑、方块造型）")));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("竖切 X", (_, _) => Do("竖切X", () => SplitTools.LayerSplit(S.Model, S.History, 0))),
            Ui.Btn("竖切 Z", (_, _) => Do("竖切Z", () => SplitTools.LayerSplit(S.Model, S.History, 2))),
            Ui.Btn("所选按轴切…", (_, _) => SliceSelection(), "只对当前选区（编辑页「选择」工具）按指定轴切")));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("合并碎件", (_, _) => Do("合并碎件", () => SplitTools.MergeTiny(S.Model, S.History))),
            Ui.Btn("碎件换向", (_, _) => Do("碎件换向", () => SplitTools.ResliceSmall(S.Model, S.History)), "挂环、手指等细小结构换个方向重切"),
            Ui.Btn("自动齿口", (_, _) => Do("齿口", () => SplitTools.FingerJointAll(S.Model, S.History)), "所有垂直相接的板之间做锯齿卡扣"),
            Ui.Btn("重新编号", (_, _) => Do("编号", () => { SplitTools.Renumber(S.Model, S.History); return 0; }))));
        root.Children.Add(Ui.Btn("清空全部零件", (_, _) => ClearAll()));

        root.Children.Add(Ui.H2("手工拆分（零件刷）"));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("零件刷 · 平面", (_, _) => { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Plane; _h.Say("零件刷：点击一颗豆，把它所在层的连通区域分给当前零件"); }),
            Ui.Btn("零件刷 · 单格", (_, _) => { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Voxel; }),
            Ui.Btn("零件刷 · 整面", (_, _) => { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Face; }),
            Ui.Btn("新建零件", (_, _) => NewPart(), "新建一个空零件并设为当前")));
        root.Children.Add(Ui.Row("平面方向", Ui.Combo(new[] { "跟随点击面", "X（侧板）", "Y（层板）", "Z（前后板）" }, 0, i => S.PlaneAxis = i - 1)));
        var same = new CheckBox { Content = "平面填充只扩散到同一零件内（用于把大板切小）", IsChecked = true };
        same.Click += (_, _) => S.PlaneSamePart = same.IsChecked == true;
        root.Children.Add(same);
        root.Children.Add(Ui.Btn("选区 → 新零件", (_, _) => SelectionToPart()));

        root.Children.Add(Ui.H2("零件列表"));
        root.Children.Add(_summary);
        _list.SelectionChanged += (_, _) => OnSelect();
        root.Children.Add(_list);
        root.Children.Add(Ui.Wrap(
            Ui.Btn("重命名", (_, _) => Rename()),
            Ui.Btn("翻面标记", (_, _) => ToggleFlip(), "图纸上标「翻」：熨烫后翻面使用"),
            Ui.Btn("隐藏/显示", (_, _) => ToggleVisible()),
            Ui.Btn("只看所选", (_, _) => Solo()),
            Ui.Btn("全部显示", (_, _) => ShowAll())));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("两件齿口", (_, _) => JointSelected(), "在列表中按住 Ctrl 选两块垂直相接的板"),
            Ui.Btn("合并所选", (_, _) => MergeSelected()),
            Ui.Btn("取消分配", (_, _) => UnassignSelected())));

        root.Children.Add(Ui.H2("显示"));
        root.Children.Add(Ui.SliderRow("爆炸预览", 1, 3, 1, v => S.Explode = (float)v, 0.05, "0.00").Row);
        var tint = new CheckBox { Content = "按零件着色", IsChecked = true };
        tint.Click += (_, _) => S.Display = tint.IsChecked == true ? DisplayMode.PartTint : DisplayMode.Color;
        root.Children.Add(tint);

        Content = Ui.Page(root);
        S.ModelChanged += () => { if (IsVisible) Dispatcher.BeginInvoke(Refresh); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    public void Enter()
    {
        S.Display = DisplayMode.PartTint;
        if (S.Action is EditAction.Attach or EditAction.Erase or EditAction.Paint) { S.Action = EditAction.PartPaint; S.Brush = BrushMode.Plane; }
        var (how, flat) = SplitTools.Recommend(S.Model);
        _recommend.Text = $"表面平面度 {flat:P0}，推荐{(how == "panel" ? "「面板拆分」（箱体）" : "「水平分层」（环片）")}";
        Refresh();
    }

    private void Auto()
    {
        var (how, _) = SplitTools.Recommend(S.Model);
        Do("智能拆分", () =>
        {
            int n = how == "panel" ? SplitTools.PanelSplit(S.Model, S.History) : SplitTools.LayerSplit(S.Model, S.History, 1);
            if (how == "panel") SplitTools.FingerJointAll(S.Model, S.History);
            return n;
        });
    }

    private void Do(string name, Func<int> f)
    {
        if (S.Model.CountSolid() == 0) return;
        int r = f();
        S.RaiseChanged();
        Refresh();
        _h.Say($"{name}：{r}，当前 {S.Model.PartList.Count} 个零件");
    }

    private void ClearAll()
    {
        if (MessageBox.Show("清空所有零件分配？（可撤销）", "豆豆工坊", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
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
        S.Action = EditAction.PartPaint;
        Refresh();
    }

    private void SliceSelection()
    {
        if (S.Selection.Count == 0) { MessageBox.Show("先在「模型编辑」里用选择工具选中一块区域"); return; }
        var t = Prompt.Ask(Window.GetWindow(this)!, "按轴切", "切割轴：X / Y / Z", "Y");
        if (t == null) return;
        int axis = t.Trim().ToUpperInvariant() switch { "X" => 0, "Z" => 2, _ => 1 };
        Do("选区切分", () => SplitTools.SliceByAxis(S.Model, S.History, axis, S.Selection.ToList()));
    }

    private void SelectionToPart()
    {
        if (S.Selection.Count == 0) { MessageBox.Show("先选中一些豆"); return; }
        S.History.Begin("选区 → 新零件");
        var p = S.Model.NewPart();
        foreach (var i in S.Selection) S.History.SetPart(i, (ushort)p.Id);
        S.Model.PruneEmptyParts();
        S.History.Commit();
        S.CurrentPart = p.Id;
    }

    private sealed class Row
    {
        public PartReport R = null!;
        public PartInfo Info = null!;
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _rep = PartAnalyzer.Analyze(S.Model);
            int errors = _rep.Parts.Count(p => p.Issues.Any(i => i.Level == IssueLevel.Error));
            int warns = _rep.Parts.Count(p => p.Issues.Any(i => i.Level == IssueLevel.Warning));
            _summary.Text = $"{_rep.Parts.Count} 个零件 · 已分配 {_rep.AssignedBeads}/{_rep.TotalBeads} 颗" +
                            (_rep.Unassigned.Count > 0 ? $" · 未分配 {_rep.Unassigned.Count}" : "") +
                            (errors > 0 ? $" · ❌ {errors} 个不可熨烫" : "") + (warns > 0 ? $" · ⚠ {warns} 个过小" : "");
            var selectedIds = _list.SelectedItems.Cast<ListBoxItem>().Select(i => ((Row)i.Tag).Info.Id).ToHashSet();
            _list.Items.Clear();
            foreach (var r in _rep.Parts)
            {
                var info = S.Model.FindPart(r.Id);
                if (info == null) continue;
                var sw = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = Ui.Brush(info.Tint), Margin = new Thickness(0, 0, 8, 0) };
                var status = r.Issues.Any(i => i.Level == IssueLevel.Error) ? "❌" : r.Issues.Any(i => i.Level == IssueLevel.Warning) ? "⚠" : "";
                var txt = new TextBlock
                {
                    Text = $"{info.Name}  {SplitTools.AxisChinese(r.PlaneAxis)}  {r.SheetSize.Cols}×{r.SheetSize.Rows}  {r.Beads}颗 {(info.Flip ? "翻" : "")}{(info.Visible ? "" : "（隐藏）")} {status}",
                    ToolTip = r.Issues.Count > 0 ? string.Join("\n", r.Issues.Select(i => i.Message)) : null,
                    Opacity = info.Visible ? 1 : 0.5,
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(sw); sp.Children.Add(txt);
                var item = new ListBoxItem { Content = sp, Tag = new Row { R = r, Info = info }, Foreground = Ui.Res("Fg") };
                _list.Items.Add(item);
                if (selectedIds.Contains(info.Id) || (selectedIds.Count == 0 && info.Id == S.CurrentPart)) item.IsSelected = true;
            }
        }
        finally { _refreshing = false; }
    }

    private List<Row> Selected() => _list.SelectedItems.Cast<ListBoxItem>().Select(i => (Row)i.Tag).ToList();

    private void OnSelect()
    {
        if (_refreshing) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        S.CurrentPart = sel[0].Info.Id;
        S.Highlight.Clear();
        S.RaiseChanged();
    }

    private void Rename()
    {
        var sel = Selected();
        if (sel.Count != 1) return;
        var n = Prompt.Ask(Window.GetWindow(this)!, "重命名", "零件名称", sel[0].Info.Name);
        if (string.IsNullOrWhiteSpace(n)) return;
        S.History.Begin("重命名零件");
        sel[0].Info.Name = n.Trim();
        S.History.Commit();
        Refresh();
    }

    private void ToggleFlip()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        S.History.Begin("翻面标记");
        foreach (var r in sel) r.Info.Flip = !r.Info.Flip;
        S.History.Commit();
        Refresh();
    }

    private void ToggleVisible()
    {
        foreach (var r in Selected()) r.Info.Visible = !r.Info.Visible;
        S.RaiseChanged(); Refresh();
    }

    private void Solo()
    {
        var ids = Selected().Select(r => r.Info.Id).ToHashSet();
        if (ids.Count == 0) return;
        foreach (var p in S.Model.PartList) p.Visible = ids.Contains(p.Id);
        S.RaiseChanged(); Refresh();
    }

    private void ShowAll()
    {
        foreach (var p in S.Model.PartList) p.Visible = true;
        S.RaiseChanged(); Refresh();
    }

    private void JointSelected()
    {
        var sel = Selected();
        if (sel.Count != 2) { MessageBox.Show("请按住 Ctrl 在列表中选择两块零件"); return; }
        int n = SplitTools.FingerJoint(S.Model, S.History, sel[0].Info.Id, sel[1].Info.Id);
        _h.Say(n > 0 ? $"齿口完成：调整了 {n} 颗" : "这两块不是垂直相接的平板，或齿口会把零件切断，未修改");
        Refresh();
    }

    private void MergeSelected()
    {
        var sel = Selected();
        if (sel.Count < 2) return;
        int target = sel[0].Info.Id;
        S.History.Begin("合并零件");
        foreach (var r in sel.Skip(1))
            foreach (var i in r.R.Cells) S.History.SetPart(i, (ushort)target);
        S.Model.PruneEmptyParts();
        S.History.Commit();
        Refresh();
    }

    private void UnassignSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        S.History.Begin("取消分配");
        foreach (var r in sel) foreach (var i in r.R.Cells) S.History.SetPart(i, 0);
        S.Model.PruneEmptyParts();
        S.History.Commit();
        Refresh();
    }
}
