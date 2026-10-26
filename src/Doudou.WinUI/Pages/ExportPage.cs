using Doudou.Core.Drawing;
using Doudou.Core.Services;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Pages;

/// <summary>⑤ 图纸导出。</summary>
public sealed class ExportPage : IStepPage
{
    public string Header => "图纸导出";
    public string Description => "生成总图、零件图、装配图和材料清单";
    public UIElement View { get; }
    public UIElement? Footer { get; }

    private readonly IHost _h;
    private Session S => _h.S;
    private readonly TextBox _title = new(), _author = new(), _modeler = new(), _serial = new(), _notes = new(), _wm = new();
    private readonly TextBox _folder = new();
    private readonly StackPanel _out = new() { Spacing = 2 };
    private int _cell = 26, _cols = 6;
    private float _spread = 2f;
    private bool _letters = true, _pdf = true, _tint;

    public ExportPage(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        root.Children.Add(Card("包含内容", null,
            Caption("· 总图：标题 + 跨品牌材料表（MARD / COCO / 卡卡 / 漫漫 / 盼盼 / 味小窝）+ 零件颗粒图 + 爆炸图 + 成品效果\n" +
                    "· 零部件拆分图、分层颗粒图、整体拼装示意图（含逐步装配）、材料 / 尺寸清单\n" +
                    "· 每个零件的单独高清编号图、CSV 清单、A3 整卷 PDF")));
        _title.PlaceholderText = "作品名";
        root.Children.Add(Card("图纸信息", null,
            Row("作品名", _title), Row("建模", _modeler), Row("拆图", _author), Row("编号", _serial), Row("声明", _notes), Row("水印", _wm)));
        root.Children.Add(Card("版式", null,
            SliderRow("颗粒格", 14, 48, _cell, v => _cell = (int)v, 1, "0", " px").Row,
            SliderRow("步骤每行", 3, 10, _cols, v => _cols = (int)v).Row,
            SliderRow("爆炸间距", 1.2, 3.5, _spread, v => _spread = (float)v, 0.1, "0.0", "×").Row,
            Check("格内标色号字母", _letters, v => _letters = v),
            Check("爆炸图按零件着色", _tint, v => _tint = v),
            Check("同时生成 A3 整卷 PDF", _pdf, v => _pdf = v)));
        var pick = Btn("选择…", async () => await Browse(), G.Folder);
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(_folder);
        Grid.SetColumn(pick, 1);
        g.Children.Add(pick);
        root.Children.Add(Card("输出位置", null, g, _out));
        View = root;
        var run = WideBtn("生成图纸", () => _ = Export(), G.Print);
        run.Margin = new Thickness(0);
        Footer = run;
        S.ModelReplaced += () => h.View.DispatcherQueue.TryEnqueue(() =>
        {
            _folder.Text = "";
            _out.Children.Clear();
        });
        S.ContentChanged += () => h.View.DispatcherQueue.TryEnqueue(() =>
        {
            if (_out.Children.Count == 0) return;
            _out.Children.Clear();
            _out.Children.Add(Caption("模型已修改，请重新生成图纸。"));
        });
    }

    public void OnEnter()
    {
        var m = S.Meta;
        _title.Text = m.Title;
        _author.Text = string.IsNullOrEmpty(m.Author) ? S.Settings.Author : m.Author;
        _modeler.Text = m.Modeler; _serial.Text = m.Serial; _notes.Text = m.Notes;
        _wm.Text = string.IsNullOrEmpty(m.Watermark) ? S.Settings.Watermark : m.Watermark;
        if (string.IsNullOrWhiteSpace(_folder.Text))
            _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Theme.AppName + "图纸", Sanitize(m.Title));
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task Browse()
    {
        var p = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(p, _h.Hwnd);
        p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) _folder.Text = Path.Combine(f.Path, Sanitize(string.IsNullOrWhiteSpace(_title.Text) ? "图纸" : _title.Text));
    }

    private async Task Export()
    {
        if (S.Model.CountSolid() == 0) { _h.Notify("模型是空的", InfoBarSeverity.Warning); return; }
        var folder = _folder.Text.Trim();
        if (folder.Length == 0)
        {
            // 空路径会让图纸写到程序目录里，用户根本找不到
            _h.Notify("请先选择输出位置", InfoBarSeverity.Warning);
            await Browse();
            folder = _folder.Text.Trim();
            if (folder.Length == 0) return;
        }
        if (S.Model.PartList.Count == 0 && !await _h.ConfirmAsync("还没有拆分零件", "图纸会把整个模型当成一个零件，建议先到「零件拆分」。仍然继续？", "继续导出")) return;
        var m = S.Meta;
        m.Title = _title.Text.Trim(); m.Author = _author.Text.Trim(); m.Modeler = _modeler.Text.Trim();
        m.Serial = _serial.Text.Trim(); m.Notes = _notes.Text.Trim(); m.Watermark = _wm.Text.Trim();
        S.Dirty = true;
        _out.Children.Clear();
        var opt = new DrawingOptions
        {
            Cell = _cell, StepColumns = _cols, ExplodeSpread = _spread, CellLetters = _letters, Pdf = _pdf, Tint = _tint,
            PitchMm = S.Settings.BeadPitchMm,
        };
        var model = S.Model.Clone();
        await _h.RunAsync("生成图纸", async (progress, ct) =>
        {
            var outp = await Task.Run(() =>
            {
                using var comp = new DrawingComposer(model, m, S.Lib, opt);
                return comp.ExportAll(folder, progress);
            }, ct);
            _h.Accounts.RecordHistory(new HistoryEntry
            {
                Kind = "export",
                Title = string.IsNullOrWhiteSpace(m.Title) ? "图纸" : m.Title,
                Summary = $"整套图纸 · {outp.Files.Count} 个文件",
                Meta = new HistoryMeta
                {
                    Beads = model.CountSolid(),
                    Colors = model.Palette.Count - 1,
                    Parts = model.PartList.Count,
                    Source = "drawing",
                },
            });
            _out.Children.Clear();
            var potg = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 4) };
            potg.Children.Add(Theme.Tag("导出完成 · EXPORTED", Theme.Orange, 13));
            potg.Children.Add(Theme.Heading(m.Title, $"{model.CountSolid():N0} beads · {model.PartList.Count} parts", 24));
            _out.Children.Add(potg);
            _out.Children.Add(Caption($"已生成 {outp.Files.Count} 个文件（另有每个零件的单图）："));
            foreach (var f in outp.Files) _out.Children.Add(new TextBlock { Text = "· " + Path.GetFileName(f), FontSize = 12 });
            _out.Children.Add(Flow(Btn("预览图纸", () => _h.ShowPreview(outp.Files.Where(f => f.EndsWith(".png")).ToList(), folder), G.Eye),
                                   Btn("打开文件夹", () => System.Diagnostics.Process.Start("explorer.exe", folder), G.Folder)));
            _h.ShowPreview(outp.Files.Where(f => f.EndsWith(".png")).ToList(), folder);
            _h.Notify("图纸已生成：" + folder, InfoBarSeverity.Success);
        });
    }
}
