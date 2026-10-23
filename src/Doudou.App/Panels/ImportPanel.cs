using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Doudou.Core.IO;
using Doudou.Core.Recon;
using Doudou.Core.Services;
using Microsoft.Win32;

using Doudou.Editor;
namespace Doudou.App.Panels;

/// <summary>① 图片导入 + 三维重建。</summary>
public sealed class ImportPanel : UserControl
{
    private sealed class Item
    {
        public string Path = "";
        public int Role;   // 0..5 = ViewKind，6 = 参考照片
    }

    private static readonly string[] Roles = { "正面", "背面", "左侧（面朝画面左）", "右侧（面朝画面右）", "顶部", "底部", "参考照片（随手拍）" };

    private readonly IHost _h;
    private readonly List<Item> _items = new();
    private readonly StackPanel _list = new();
    private readonly TextBox _log = new() { Height = 150, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 11.5 };
    private readonly TextBox _name = new() { Text = "我的作品" };
    private readonly TextBox _hint = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 44, AcceptsReturn = false };
    private int _height = 56, _method, _brand, _maxColors = 16;
    private readonly CheckBox _aiComplete = new() { Content = "AI 补全缺失的正/背/左/右视图", IsChecked = true };
    private readonly CheckBox _cartoon = new() { Content = "AI 卡通化（细节放大、平涂色，推荐）", IsChecked = true };
    private readonly CheckBox _flat = new() { Content = "AI 平涂取色（只影响颜色）" };
    private readonly CheckBox _sym = new() { Content = "近似回转对称（铃铛/瓶罐）：由正面推断侧面形状" };
    private readonly List<string> _brandIds;

    public ImportPanel(IHost h)
    {
        _h = h;
        _brandIds = h.S.Lib.Brands.Keys.OrderBy(k => k).ToList();
        _brand = Math.Max(0, _brandIds.IndexOf(h.S.Settings.DefaultBrand));

        var root = new StackPanel();
        root.Children.Add(Ui.H1("图片导入与三维重建"));
        root.Children.Add(Ui.Hint("拖入 3~6 张同一物体的图片。标准视图（正/背/左/右）效果最好；角度随意的实拍照片请标为「参考照片」，由 AI 先整理成标准视图。"));
        root.Children.Add(Ui.Wrap(
            Ui.Btn("添加图片…", (_, _) => Browse()),
            Ui.Btn("清空", (_, _) => { _items.Clear(); Refresh(); })));
        root.Children.Add(_list);

        root.Children.Add(Ui.H2("作品"));
        root.Children.Add(Ui.Row("名称", _name));
        root.Children.Add(Ui.Row("主体描述", _hint));
        root.Children.Add(Ui.Hint("可选：用一句话描述物体（中英文均可），帮助 AI 理解，例如「铃铛形风铃挂件，顶部有挂环和一圈花瓣，下面吊着一条小金鱼」"));

        root.Children.Add(Ui.H2("重建参数"));
        root.Children.Add(Ui.SliderRow("成品高度", 16, 128, _height, v => _height = (int)v).Row);
        root.Children.Add(Ui.Hint("单位：颗（层）。豆径 2.6mm，56 颗 ≈ 14.6cm。越高细节越多、豆量越大。"));
        root.Children.Add(Ui.Row("建模方式", Ui.Combo(new[] { "自动（优先混元3D）", "混元3D 本地模型", "轮廓雕刻（无需显卡）" }, 0, i => _method = i)));
        root.Children.Add(Ui.Row("色卡", Ui.Combo(_brandIds.Select(id => h.S.Lib.Brands[id].Title), _brand, i => _brand = i)));
        root.Children.Add(Ui.SliderRow("最多颜色", 4, 40, _maxColors, v => _maxColors = (int)v).Row);
        root.Children.Add(_aiComplete);
        root.Children.Add(_cartoon);
        root.Children.Add(_flat);
        root.Children.Add(_sym);
        root.Children.Add(Ui.Btn("开始重建", async (_, _) => await Run(), primary: true));
        root.Children.Add(Ui.H2("日志"));
        root.Children.Add(_log);
        Content = Ui.Page(root);
        Refresh();
    }

    private void Browse()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dlg.ShowDialog() == true) AddImages(dlg.FileNames);
    }

    public void AddImages(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            // 默认按顺序猜：第一张正面，第二张左侧，第三张背面，第四张右侧；文件名里有关键词时以关键词为准
            var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            int role = n.Contains("front") || n.Contains("正") ? 0 : n.Contains("back") || n.Contains("背") ? 1
                : n.Contains("left") || n.Contains("左") ? 2 : n.Contains("right") || n.Contains("右") ? 3
                : n.Contains("top") || n.Contains("顶") ? 4 : -1;
            if (role < 0)
            {
                var used = _items.Select(i => i.Role).ToHashSet();
                role = new[] { 0, 2, 1, 3, 4, 5 }.FirstOrDefault(r => !used.Contains(r), 6);
            }
            _items.Add(new Item { Path = f, Role = role });
        }
        if (_items.Count > 0 && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Path.GetFileNameWithoutExtension(_items[0].Path);
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        if (_items.Count == 0)
        {
            var drop = new Border
            {
                BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 28, 16, 28), Margin = new Thickness(0, 6, 0, 6),
                Child = new TextBlock { Text = "把图片拖到窗口任意位置", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Res("Muted") },
            };
            _list.Children.Add(drop);
            return;
        }
        foreach (var it in _items.ToList())
        {
            var img = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(it.Path); b.DecodePixelWidth = 128; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
                img.Source = b;
            }
            catch (Exception) { }
            var role = Ui.Combo(Roles, it.Role, r => it.Role = r);
            var del = Ui.Btn("✕", (_, _) => { _items.Remove(it); Refresh(); });
            del.Padding = new Thickness(6, 2, 6, 2);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = Path.GetFileName(it.Path), TextTrimming = TextTrimming.CharacterEllipsis, Width = 190 });
            info.Children.Add(role);
            var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            DockPanel.SetDock(img, Dock.Left); DockPanel.SetDock(del, Dock.Right);
            dp.Children.Add(img); dp.Children.Add(del); dp.Children.Add(info);
            _list.Children.Add(new Border { Style = (Style)Application.Current.Resources["Card"], Child = dp, Padding = new Thickness(6) });
        }
        var roles = _items.Where(i => i.Role < 6).GroupBy(i => i.Role).Where(g => g.Count() > 1).ToList();
        if (roles.Count > 0) _list.Children.Add(Ui.Hint("⚠ 有重复的视图角色，只会使用每种角色的第一张"));
    }

    private void Log(string s) => Dispatcher.Invoke(() => { _log.AppendText(s + "\n"); _log.ScrollToEnd(); });

    private async Task Run()
    {
        if (_items.Count == 0) { MessageBox.Show("请先添加图片"); return; }
        var s = _h.S;
        if (!s.Model.PartList.Any() && s.Model.CountSolid() > 0 && s.Dirty)
        {
            if (MessageBox.Show("重建会替换当前模型，继续吗？", "豆豆工坊", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        }
        var inputs = _items.Where(i => i.Role < 6).GroupBy(i => i.Role).Select(g => ((ViewKind)g.Key, g.First().Path)).ToList();
        var refs = _items.Where(i => i.Role == 6).Select(i => i.Path).ToList();
        var opt = new ReconOptions
        {
            Method = _method switch { 1 => ReconMethod.Hunyuan, 2 => ReconMethod.VisualHull, _ => ReconMethod.Auto },
            TargetHeight = _height,
            AiCompleteViews = _aiComplete.IsChecked == true,
            AiFlatColors = _flat.IsChecked == true,
            CartoonStyle = _cartoon.IsChecked == true,
            InferSymmetricSides = _sym.IsChecked == true,
            ReferencePhotos = refs,
            SubjectHint = _hint.Text.Trim(),
            Name = _name.Text.Trim(),
            Quantize = new QuantizeOptions { BrandId = _brandIds[_brand], MaxColors = _maxColors },
        };
        _log.Clear();
        await _h.RunAsync("三维重建", async progress =>
        {
            var p = new Progress<string>(m => { Log(m); ((IProgress<string>)progress).Report(m); });
            var pipe = new ReconstructPipeline(s.Lib, s.Hy3d, new ImageAi(s.Settings));
            var res = await Task.Run(() => pipe.RunAsync(inputs, opt, p));
            var views = res.Views.Where(v => !v.ShapeOnly).GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.First().SourcePath);
            var meta = new ProjectMeta { Title = opt.Name, Author = s.Settings.Author, Watermark = s.Settings.Watermark };
            s.Replace(res.Model, meta, views);
            Log($"完成：{res.Model.SX}×{res.Model.SY}×{res.Model.SZ}，{res.Model.CountSolid()} 颗，{res.Model.Palette.Count - 1} 色（{res.MethodUsed}）");
            _h.Say("重建完成，进入编辑");
            _h.GoStep(2);
        });
    }
}
