using System.Windows;
using System.Windows.Controls;
using Doudou.Core.Check;
using Doudou.Core.Parts;

using Doudou.Editor;
namespace Doudou.App.Panels;

/// <summary>④ 检查：漏色（内芯外露 / 破洞 / 漏分零件 / 悬空 / 与参考图色差）+ 装配路径验证。</summary>
public sealed class CheckPanel : UserControl
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _items = new() { Height = 260 };
    private readonly ListBox _steps = new() { Height = 220 };
    private readonly TextBlock _asmSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _withRef = new() { Content = "与参考视图逐格比色（外观一致性）", IsChecked = true };
    private LeakReport? _rep;

    public CheckPanel(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        root.Children.Add(Ui.H1("检查"));
        root.Children.Add(Ui.Hint("漏色检查：① 任意色内芯是否露在外面；② 外壳是否有洞能看到内部；③ 是否有豆没分到零件、零件能否熨烫；④ 悬空的豆；⑤ 外观与参考图是否一致。点击条目可在视图中定位（红色高亮）。"));
        root.Children.Add(_withRef);
        root.Children.Add(Ui.Btn("开始检查", async (_, _) => await Run(), primary: true));
        root.Children.Add(_summary);
        _items.SelectionChanged += (_, _) => Locate();
        root.Children.Add(_items);
        root.Children.Add(Ui.Wrap(
            Ui.Btn("高亮全部问题", (_, _) => HighlightAll()),
            Ui.Btn("清除高亮", (_, _) => { S.Highlight.Clear(); S.RaiseChanged(); })));

        root.Children.Add(Ui.H2("装配验证"));
        root.Children.Add(Ui.Hint("逐件模拟：每一步新零件必须贴住已装好的零件，并且能沿某个方向直线装进去而不撞到别的零件。"));
        root.Children.Add(Ui.Btn("验证装配顺序", (_, _) => Assembly()));
        root.Children.Add(_asmSummary);
        _steps.SelectionChanged += (_, _) => ShowStep();
        root.Children.Add(_steps);
        Content = Ui.Page(root);
    }

    private async Task Run()
    {
        var m = S.Model;
        var refs = _withRef.IsChecked == true ? S.RefViews() : new();
        await _h.RunAsync("漏色检查", async _ =>
        {
            _rep = await Task.Run(() => LeakChecker.Check(m, refs.Count > 0 ? refs : null));
            _summary.Text = $"共 {_rep.TotalBeads} 颗，外表可见 {_rep.VisibleBeads} 颗。\n{_rep.Summary()}";
            _summary.Foreground = _rep.Items.Any(i => i.Kind != LeakKind.ColorMismatch) ? Ui.Res("Danger") : Ui.Res("Ok");
            _items.Items.Clear();
            foreach (var g in _rep.Items.GroupBy(i => i.Kind))
            {
                int n = 0;
                foreach (var it in g.OrderByDescending(i => i.Severity))
                {
                    if (++n > 300) { _items.Items.Add(new ListBoxItem { Content = $"… 还有 {g.Count() - 300} 条", IsEnabled = false }); break; }
                    _items.Items.Add(new ListBoxItem { Content = it.Message, Tag = it, Foreground = Ui.Res(it.Kind == LeakKind.ColorMismatch ? "Muted" : "Fg") });
                }
            }
            if (refs.Count == 0 && _withRef.IsChecked == true) _h.Say("没有参考视图，跳过外观比色");
            HighlightAll();
        });
    }

    private void HighlightAll()
    {
        if (_rep == null) return;
        S.Highlight.Clear();
        foreach (var it in _rep.Items.Where(i => i.Kind != LeakKind.ColorMismatch || i.Severity > 0.6))
            if (S.Model.InBounds(it.Cell)) S.Highlight.Add(S.Model.Index(it.Cell));
        S.RaiseChanged();
    }

    private void Locate()
    {
        if (_items.SelectedItem is not ListBoxItem { Tag: LeakItem it }) return;
        S.Highlight.Clear();
        var m = S.Model;
        if (m.InBounds(it.Cell))
        {
            int i = m.Index(it.Cell);
            if (m.Colors[i] != 0) S.Highlight.Add(i);
            else
                foreach (var d in Doudou.Core.Geometry.Dir6.All)
                {
                    var n = it.Cell + d;
                    if (m.InBounds(n) && m.Colors[m.Index(n)] != 0) S.Highlight.Add(m.Index(n));
                }
        }
        S.RaiseChanged();
        _h.View.FocusCell(it.Cell);
    }

    private AssemblyPlan? _plan;
    private ModelPartsReport? _parts;

    private void Assembly()
    {
        _parts = PartAnalyzer.Analyze(S.Model);
        if (_parts.Parts.Count == 0) { _asmSummary.Text = "还没有拆分零件，请先在「零件拆分」里拆件。"; return; }
        _plan = AssemblyPlanner.Plan(S.Model, _parts);
        _asmSummary.Text = _plan.Summary + $"（用时 {_plan.Seconds:F2}s）";
        _steps.Items.Clear();
        foreach (var st in _plan.Steps)
            _steps.Items.Add(new ListBoxItem
            {
                Content = $"{st.Index:00}. {st.Instruction}",
                Tag = st,
                Foreground = Ui.Res(st.Status == StepStatus.Complex ? "Danger" : "Fg"),
            });
    }

    /// <summary>选中某一步：只显示到这一步为止的零件，并高亮当前件。</summary>
    private void ShowStep()
    {
        if (_plan == null || _steps.SelectedItem is not ListBoxItem { Tag: AssemblyStep st }) return;
        var upto = _plan.Steps.TakeWhile(s => s.Index <= st.Index).Select(s => s.PartId).ToHashSet();
        foreach (var p in S.Model.PartList) p.Visible = upto.Contains(p.Id);
        S.Highlight.Clear();
        var cur = _parts?.Parts.FirstOrDefault(p => p.Id == st.PartId);
        if (cur != null) foreach (var i in cur.Cells) S.Highlight.Add(i);
        S.RaiseChanged();
        _h.Say($"第 {st.Index} 步：{st.Instruction}（切换到其他步骤前，可在零件页点「全部显示」恢复）");
    }
}
