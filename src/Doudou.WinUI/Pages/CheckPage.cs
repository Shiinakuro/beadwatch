using Doudou.Core.Check;
using Doudou.Core.Geometry;
using Doudou.Core.Parts;
using Doudou.Core.Render;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>④ 检查：漏色 + 装配验证（可逐步回放）。</summary>
public sealed class CheckPage : IStepPage
{
    public string Header => "检查";
    public string Description => "检查漏色、漏拆和装配可行性；点击问题可在视图中定位";
    public UIElement View { get; }

    private readonly IHost _h;
    private Session S => _h.S;
    private readonly StackPanel _badges = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _banner = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl _asmBanner = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _items = new() { MaxHeight = 300, SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed };
    private readonly ListView _steps = new() { MaxHeight = 280, SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed };
    private readonly TextBlock _asm = new() { TextWrapping = TextWrapping.Wrap };
    private bool _withRef = true;
    private LeakReport? _rep;
    private AssemblyPlan? _plan;
    private ModelPartsReport? _parts;

    public CheckPage(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        _items.SelectionChanged += (_, _) => Locate();
        root.Children.Add(Card("漏色检查",
            "① 任意色内芯是否露在外面 ② 外壳破洞（能看到内部）③ 没分到零件 / 不能熨烫的零件 ④ 悬空豆 ⑤ 与参考图的颜色差异",
            Check("与参考视图逐格比色", _withRef, v => _withRef = v),
            WideBtn("开始检查", async () => await Run(), G.Check),
            _banner, _badges, _summary, _items,
            Flow(Btn("高亮全部问题", HighlightAll, G.Lightning), Btn("清除高亮", () => { S.Highlight.Clear(); S.RaiseChanged(); }))));

        _steps.SelectionChanged += (_, _) => ShowStep();
        root.Children.Add(Card("装配验证",
            "逐件模拟：每一步新零件都要贴住已装好的零件，并能沿某个方向直线装进去而不撞到其他零件。",
            Btn("验证装配顺序", async () => await Assembly(), G.Play),
            _asmBanner, _asm, _steps,
            Flow(Btn("上一步", async () => await Move(-1), G.Back), Btn("下一步", async () => await Move(1), G.Play), Btn("显示全部", () => { foreach (var p in S.Model.PartList) p.Visible = true; S.Highlight.Clear(); S.RaiseChanged(); }, G.Eye)),
            Caption("装配动图：按上面的装配顺序把零件逐个「装」回成品，导出可循环 GIF 与逐帧 PNG，可直接发给客户或贴进教程。"),
            Flow(Btn("导出装配动图", async () => await ExportGif(), G.Camera))));
        View = root;
    }

    public void OnEnter() { }

    private static Border Badge(string text, bool bad) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 12 },
        Background = bad ? Solid(0x5A, 0x22, 0x1E) : Solid(0x1E, 0x46, 0x2E),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 3, 10, 3),
    };

    private async Task Run()
    {
        var m = S.Model;
        var refs = _withRef ? S.RefViews() : new();
        await _h.RunAsync("漏色检查", async (p, ct) =>
        {
            p.Report("分析外表面、空腔与零件…");
            _rep = await Task.Run(() => LeakChecker.Check(m, refs.Count > 0 ? refs : null), ct);
            _badges.Children.Clear();
            foreach (var (k, label) in new[] { (LeakKind.CoreExposed, "内芯外露"), (LeakKind.ShellHole, "破洞"), (LeakKind.Unassigned, "未分零件"), (LeakKind.BadPart, "问题零件"), (LeakKind.Floating, "悬空") })
            {
                int c = _rep.Count(k);
                _badges.Children.Add(Badge($"{label} {c}", c > 0));
            }
            _summary.Text = $"共 {_rep.TotalBeads:N0} 颗，外表可见 {_rep.VisibleBeads:N0} 颗。{_rep.Summary()}";
            _items.Items.Clear();
            foreach (var g in _rep.Items.GroupBy(i => i.Kind))
                foreach (var it in g.OrderByDescending(i => i.Severity).Take(200))
                    _items.Items.Add(new ListViewItem { Content = new TextBlock { Text = it.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = it.Kind == LeakKind.ColorMismatch ? 0.7 : 1 }, Tag = it });
            _items.Visibility = _items.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (refs.Count == 0 && _withRef) _h.Notify("这个工程没有参考视图，已跳过外观比色");
            HighlightAll();
            bool clean = _rep.Items.All(i => i.Kind == LeakKind.ColorMismatch);
            int bad = _rep.Items.Count(i => i.Kind != LeakKind.ColorMismatch);
            _banner.Content = Theme.Banner(clean, clean ? "没有漏色、破洞或漏拆的豆子。" : $"发现 {bad} 处问题，已在视图中标红；点条目可定位。");
            _h.Notify(clean ? "没有发现漏色问题" : "发现问题，已在视图中用红色高亮", clean ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        });
    }

    private void HighlightAll()
    {
        if (_rep == null) return;
        S.Highlight.Clear();
        foreach (var it in _rep.Items.Where(i => i.Kind != LeakKind.ColorMismatch || i.Severity > 0.6))
            AddHighlight(it.Cell);
        S.RaiseChanged();
    }

    private void AddHighlight(Int3 c)
    {
        var m = S.Model;
        if (!m.InBounds(c)) return;
        if (m.Colors[m.Index(c)] != 0) { S.Highlight.Add(m.Index(c)); return; }
        foreach (var d in Dir6.All)
        {
            var n = c + d;
            if (m.InBounds(n) && m.Colors[m.Index(n)] != 0) S.Highlight.Add(m.Index(n));
        }
    }

    private void Locate()
    {
        if (_items.SelectedItem is not ListViewItem { Tag: LeakItem it }) return;
        S.Highlight.Clear();
        AddHighlight(it.Cell);
        S.RaiseChanged();
        _h.View.FocusCell(it.Cell);
    }

    private async Task Assembly()
    {
        if (S.Model.CountSolid() == 0) { _asm.Text = "模型还是空的。"; return; }
        var model = S.Model.Clone();
        await _h.RunAsync("验证装配顺序", async (p, ct) =>
        {
            // 零件多时这一步要搜很多装入方向，放后台跑，别把界面卡住
            var (parts, plan) = await Task.Run(() =>
            {
                p.Report("分析零件…");
                var rep = PartAnalyzer.Analyze(model);
                if (rep.Parts.Count == 0) return (rep, (AssemblyPlan?)null);
                p.Report($"搜索 {rep.Parts.Count} 个零件的装入顺序…");
                return (rep, (AssemblyPlan?)AssemblyPlanner.Plan(model, rep));
            }, ct);
            _parts = parts;
            _plan = plan;
            _steps.Items.Clear();
            if (plan == null)
            {
                _steps.Visibility = Visibility.Collapsed;
                _asm.Text = "还没有拆分零件，请先到「零件拆分」。";
                _asmBanner.Content = null;
                return;
            }
            _asm.Text = plan.Summary + $"（用时 {plan.Seconds:F2} 秒）";
            _asmBanner.Content = Theme.Banner(plan.ComplexCount == 0,
                plan.ComplexCount == 0 ? $"{plan.Steps.Count} 个零件全部能直线装入。" : $"{plan.ComplexCount} 个零件没有直线装入路径，需要试装或点胶。");
            _steps.Visibility = Visibility.Visible;
            foreach (var st in plan.Steps)
                _steps.Items.Add(new ListViewItem
                {
                    Tag = st,
                    Content = new TextBlock
                    {
                        Text = $"{st.Index:00}  {st.Instruction}", TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
                        Foreground = st.Status == StepStatus.Complex ? Solid(0xFF, 0x8A, 0x7A) : Brush("TextFillColorPrimaryBrush"),
                    },
                });
        });
    }

    private async Task Move(int d)
    {
        if (_steps.Items.Count == 0)
        {
            await Assembly();
            if (_steps.Items.Count == 0) return;
        }
        _steps.SelectedIndex = Math.Clamp(_steps.SelectedIndex + d, 0, _steps.Items.Count - 1);
        _steps.ScrollIntoView(_steps.SelectedItem);
    }

    /// <summary>只显示到当前步骤为止的零件，并高亮当前步骤新加入的零件。</summary>
    private void ShowStep()
    {
        if (_plan == null || _steps.SelectedItem is not ListViewItem { Tag: AssemblyStep st }) return;
        var upto = _plan.Steps.TakeWhile(s => s.Index <= st.Index).Select(s => s.PartId).ToHashSet();
        foreach (var p in S.Model.PartList) p.Visible = upto.Contains(p.Id);
        S.Highlight.Clear();
        var cur = _parts?.Parts.FirstOrDefault(p => p.Id == st.PartId);
        if (cur != null) foreach (var i in cur.Cells) S.Highlight.Add(i);
        S.RaiseChanged();
    }

    private async Task ExportGif()
    {
        if (S.Model.CountSolid() == 0) { _h.Notify("模型还是空的", InfoBarSeverity.Warning); return; }
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _h.Hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        var dir = folder.Path;
        var model = S.Model.Clone();
        await _h.RunAsync("生成装配动图", async (progress, ct) =>
        {
            var (gif, n) = await Task.Run(() =>
            {
                var rep = PartAnalyzer.Analyze(model);
                if (rep.Parts.Count == 0) return ("", 0);
                progress.Report("规划装配顺序…");
                var plan = AssemblyPlanner.Plan(model, rep);
                progress.Report($"逐帧渲染 {plan.Steps.Count} 步装配…");
                var frames = AssemblyAnimation.BuildFrames(model, rep, plan);
                if (frames.Count == 0) return ("", 0);
                try
                {
                    progress.Report("编码 GIF…");
                    var gifPath = Path.Combine(dir, "装配动图.gif");
                    GifWriter.Save(frames, gifPath, 100);
                    var frameDir = Path.Combine(dir, "装配帧");
                    for (int i = 0; i < frames.Count; i++)
                        IsoRenderer.SavePng(frames[i], Path.Combine(frameDir, $"帧{i + 1:00}.png"));
                    return (gifPath, frames.Count);
                }
                finally { foreach (var f in frames) f.Dispose(); }
            }, ct);
            if (n == 0) { _h.Notify("还没有拆分零件，无法生成装配动图", InfoBarSeverity.Warning); return; }
            _h.Notify($"装配动图已生成：{gif}（{n} 帧，另存了逐帧 PNG）", InfoBarSeverity.Success);
        });
    }
}
