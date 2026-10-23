using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Doudou.Core.Drawing;
using Microsoft.Win32;

using Doudou.Editor;
namespace Doudou.App.Panels;

/// <summary>⑤ 图纸导出。</summary>
public sealed class ExportPanel : UserControl
{
    private readonly IHost _h;
    private Session S => _h.S;
    private readonly TextBox _title = new(), _author = new(), _modeler = new(), _serial = new(), _notes = new(), _wm = new();
    private readonly TextBox _folder = new();
    private int _cell = 26, _cols = 6;
    private float _spread = 2f;
    private readonly CheckBox _letters = new() { Content = "格内标色号字母", IsChecked = true };
    private readonly CheckBox _pdf = new() { Content = "同时生成 A3 整卷 PDF", IsChecked = true };
    private readonly CheckBox _tint = new() { Content = "爆炸图按零件着色", IsChecked = false };
    private readonly StackPanel _files = new();

    public ExportPanel(IHost h)
    {
        _h = h;
        var root = new StackPanel();
        root.Children.Add(Ui.H1("图纸导出"));
        root.Children.Add(Ui.Hint("生成与真人画手同版式的整套图纸：标题+跨品牌材料表、零件颗粒图、分层颗粒图、爆炸图、装配步骤、成品效果、材料/尺寸清单（PNG + CSV + PDF）。"));
        root.Children.Add(Ui.H2("图纸信息"));
        root.Children.Add(Ui.Row("作品名", _title));
        root.Children.Add(Ui.Row("建模", _modeler));
        root.Children.Add(Ui.Row("拆图", _author));
        root.Children.Add(Ui.Row("编号", _serial));
        root.Children.Add(Ui.Row("声明", _notes));
        root.Children.Add(Ui.Row("水印", _wm));
        root.Children.Add(Ui.H2("版式"));
        root.Children.Add(Ui.SliderRow("颗粒格大小", 14, 48, _cell, v => _cell = (int)v).Row);
        root.Children.Add(Ui.SliderRow("步骤每行", 3, 10, _cols, v => _cols = (int)v).Row);
        root.Children.Add(Ui.SliderRow("爆炸间距", 1.2, 3.5, _spread, v => _spread = (float)v, 0.1, "0.0").Row);
        root.Children.Add(_letters);
        root.Children.Add(_tint);
        root.Children.Add(_pdf);
        root.Children.Add(Ui.H2("输出"));
        var browse = Ui.Btn("…", (_, _) => Browse());
        var dp = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse); dp.Children.Add(_folder);
        root.Children.Add(Ui.Row("文件夹", dp));
        root.Children.Add(Ui.Btn("生成图纸", async (_, _) => await Export(), primary: true));
        root.Children.Add(_files);
        Content = Ui.Page(root);
    }

    public void Enter()
    {
        var m = S.Meta;
        _title.Text = m.Title; _author.Text = string.IsNullOrEmpty(m.Author) ? S.Settings.Author : m.Author;
        _modeler.Text = m.Modeler; _serial.Text = m.Serial; _notes.Text = m.Notes;
        _wm.Text = string.IsNullOrEmpty(m.Watermark) ? S.Settings.Watermark : m.Watermark;
        if (string.IsNullOrWhiteSpace(_folder.Text))
            _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "豆豆工坊图纸", Sanitize(m.Title));
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "选择图纸输出文件夹" };
        if (dlg.ShowDialog() == true) _folder.Text = Path.Combine(dlg.FolderName, Sanitize(_title.Text));
    }

    private async Task Export()
    {
        var m = S.Meta;
        m.Title = _title.Text.Trim(); m.Author = _author.Text.Trim(); m.Modeler = _modeler.Text.Trim();
        m.Serial = _serial.Text.Trim(); m.Notes = _notes.Text.Trim(); m.Watermark = _wm.Text.Trim();
        S.Dirty = true;
        if (S.Model.PartList.Count == 0 &&
            MessageBox.Show("还没有拆分零件，图纸会把整个模型当成一个零件。继续吗？", "豆豆工坊", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var folder = _folder.Text.Trim();
        var opt = new DrawingOptions
        {
            Cell = _cell, StepColumns = _cols, ExplodeSpread = _spread, CellLetters = _letters.IsChecked == true,
            Pdf = _pdf.IsChecked == true, Tint = _tint.IsChecked == true, PitchMm = S.Settings.BeadPitchMm,
        };
        var model = S.Model.Clone();
        await _h.RunAsync("生成图纸", async progress =>
        {
            var outp = await Task.Run(() =>
            {
                using var comp = new DrawingComposer(model, m, S.Lib, opt);
                return comp.ExportAll(folder, progress);
            });
            _files.Children.Clear();
            _files.Children.Add(Ui.Hint($"已生成 {outp.Files.Count} 个文件 + 零件单图："));
            foreach (var f in outp.Files) _files.Children.Add(Ui.Text("· " + Path.GetFileName(f), 12));
            _files.Children.Add(Ui.Wrap(
                Ui.Btn("打开文件夹", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true })),
                Ui.Btn("预览图纸", (_, _) => _h.ShowPreview(outp.Files.Where(f => f.EndsWith(".png")).ToList()))));
            _h.ShowPreview(outp.Files.Where(f => f.EndsWith(".png")).ToList());
            _h.Say("图纸已生成：" + folder);
        });
    }
}
