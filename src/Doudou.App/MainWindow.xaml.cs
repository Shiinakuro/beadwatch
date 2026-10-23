using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Doudou.App.Panels;
using Doudou.App.Viewport;
using Doudou.Core.Edit;
using Doudou.Core.IO;
using Doudou.Core.Recon;
using Microsoft.Win32;

using Doudou.Editor;
namespace Doudou.App;

/// <summary>面板访问主窗口的接口。</summary>
public interface IHost
{
    Session S { get; }
    VoxelViewport View { get; }
    Task RunAsync(string title, Func<IProgress<string>, Task> work);
    void ShowPreview(IReadOnlyList<string> images);
    void Say(string text);
    void GoStep(int step);
}

public partial class MainWindow : Window, IHost
{
    public Session S { get; }
    VoxelViewport IHost.View => View;

    private readonly ImportPanel _import;
    private readonly EditPanel _edit;
    private readonly PartsPanel _parts;
    private readonly CheckPanel _check;
    private readonly ExportPanel _export;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        S = new Session();
        S.Status += Say;
        S.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Session.Dirty)) UpdateTitle(); };
        S.ModelChanged += UpdateUndoMenu;
        S.ModelReplaced += UpdateTitle;
        View.Attach(S);

        _import = new ImportPanel(this);
        _edit = new EditPanel(this);
        _parts = new PartsPanel(this);
        _check = new CheckPanel(this);
        _export = new ExportPanel(this);
        PanelHost.Content = _import;

        PreviewKeyDown += OnKey;
        KeyUp += (_, e) => { if (e.Key == Key.Space) View.SetSpace(false); };
        Drop += OnDrop;
        Closing += OnClosing;
        UpdateTitle();
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                if (args[1].EndsWith(".doudou", StringComparison.OrdinalIgnoreCase)) OpenProject(args[1]);
                else if (args[1].EndsWith(".vox", StringComparison.OrdinalIgnoreCase))
                {
                    var m = VoxFile.Read(args[1]);
                    Quantizer.RemapToBrand(m, S.Lib, S.Settings.DefaultBrand);
                    S.Replace(m, new ProjectMeta { Title = Path.GetFileNameWithoutExtension(args[1]) }, new());
                }
                if (args.Length > 2 && int.TryParse(args[2], out var step)) GoStep(step);
            }
        };
        Say($"已加载 {S.Lib.Brands.Count} 套色卡。" + (Doudou.Core.Services.Hy3dClient.IsInstalled(out var why) ? "混元3D 本地模型就绪。" : "混元3D 不可用：" + why));
    }

    // ───────────── IHost ─────────────

    public void Say(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    public async Task RunAsync(string title, Func<IProgress<string>, Task> work)
    {
        if (_busy) { Say("请等待当前任务完成"); return; }
        _busy = true;
        Busy.Visibility = Visibility.Visible;
        IsEnabledCore(false);
        Say(title + "…");
        var progress = new Progress<string>(Say);
        try
        {
            await work(progress);
        }
        catch (Exception e)
        {
            Say("失败：" + e.Message);
            MessageBox.Show(this, e.Message, title + "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Busy.Visibility = Visibility.Collapsed;
            IsEnabledCore(true);
        }
    }

    private void IsEnabledCore(bool on)
    {
        PanelHost.IsEnabled = on;
        View.IsEnabled = on;
    }

    public void ShowPreview(IReadOnlyList<string> images)
    {
        PreviewPick.ItemsSource = images.Select(Path.GetFileName).ToList();
        PreviewPick.Tag = images;
        PreviewHost.Visibility = Visibility.Visible;
        if (images.Count > 0) PreviewPick.SelectedIndex = 0;
    }

    public void GoStep(int step)
    {
        var rb = step switch { 1 => Step1, 2 => Step2, 3 => Step3, 4 => Step4, _ => Step5 };
        rb.IsChecked = true;
    }

    // ───────────── 步骤切换 ─────────────

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if (PanelHost == null) return;
        PreviewHost.Visibility = Visibility.Collapsed;
        if (sender == Step1) PanelHost.Content = _import;
        else if (sender == Step2) { PanelHost.Content = _edit; if (S.Action == EditAction.PartPaint) S.Action = EditAction.Attach; S.Display = DisplayMode.Color; }
        else if (sender == Step3) { PanelHost.Content = _parts; _parts.Enter(); }
        else if (sender == Step4) { PanelHost.Content = _check; }
        else if (sender == Step5) { PanelHost.Content = _export; _export.Enter(); }
        if (sender != Step3 && S.Explode > 1.01f) S.Explode = 1;
        if (sender != Step3 && sender != Step4) S.Display = DisplayMode.Color;
    }

    // ───────────── 文件 ─────────────

    private void UpdateTitle()
    {
        var name = S.ProjectPath != null ? Path.GetFileName(S.ProjectPath) : S.Meta.Title;
        Title = $"豆豆工坊 · {name}{(S.Dirty ? " *" : "")}";
    }

    private void UpdateUndoMenu()
    {
        MiUndo.Header = S.History.CanUndo ? $"撤销 {S.History.UndoName}" : "撤销";
        MiRedo.Header = S.History.CanRedo ? $"重做 {S.History.RedoName}" : "重做";
        MiUndo.IsEnabled = S.History.CanUndo;
        MiRedo.IsEnabled = S.History.CanRedo;
    }

    private bool ConfirmDiscard()
    {
        if (!S.Dirty || S.Model.CountSolid() == 0) return true;
        var r = MessageBox.Show(this, "当前模型有未保存的修改，是否先保存？", "豆豆工坊", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save(false);
        return true;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new NewModelDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        S.NewBlank(dlg.SX, dlg.SY, dlg.SZ);
        GoStep(2);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "豆豆工坊工程 (*.doudou)|*.doudou" };
        if (dlg.ShowDialog(this) != true) return;
        OpenProject(dlg.FileName);
    }

    public void OpenProject(string path)
    {
        try
        {
            var dir = Doudou.Core.Services.AppPaths.NewWorkDir("open");
            var (m, meta, views) = ProjectFile.Load(path, dir);
            S.Replace(m, meta, views, path);
            Say($"已打开 {Path.GetFileName(path)}：{m.CountSolid()} 颗，{m.PartList.Count} 个零件");
            GoStep(2);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开失败：" + ex.Message, "豆豆工坊");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save(false);
    private void OnSaveAs(object sender, RoutedEventArgs e) => Save(true);

    private bool Save(bool saveAs)
    {
        var path = S.ProjectPath;
        if (saveAs || path == null)
        {
            var dlg = new SaveFileDialog { Filter = "豆豆工坊工程 (*.doudou)|*.doudou", FileName = S.Meta.Title + ".doudou" };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            ProjectFile.Save(path, S.Model, S.Meta, S.ViewFiles);
            S.ProjectPath = path;
            S.Dirty = false;
            UpdateTitle();
            Say("已保存 " + path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "豆豆工坊");
            return false;
        }
    }

    private void OnImportVox(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "MagicaVoxel (*.vox)|*.vox" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var m = VoxFile.Read(dlg.FileName);
            Doudou.Core.Recon.Quantizer.RemapToBrand(m, S.Lib, S.Settings.DefaultBrand);
            S.Replace(m, new ProjectMeta { Title = Path.GetFileNameWithoutExtension(dlg.FileName) }, new());
            Say($"已导入 {m.CountSolid()} 颗，颜色已映射到 {S.Lib.Get(m.BrandId).Title}");
            GoStep(2);
        }
        catch (Exception ex) { MessageBox.Show(this, "导入失败：" + ex.Message, "豆豆工坊"); }
    }

    private void OnImportMesh(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "三维网格 (*.obj;*.glb;*.stl)|*.obj;*.glb;*.stl" };
        if (dlg.ShowDialog(this) != true) return;
        var input = Prompt.Ask(this, "导入网格", "成品高度（豆数 / 层数）", "48");
        if (!int.TryParse(input, out int h) || h < 5) return;
        _ = RunAsync("导入网格", async p =>
        {
            var file = dlg.FileName;
            var m = await Task.Run(() =>
            {
                var mesh = MeshLoader.Load(file);
                var occ = Voxelizer.Voxelize(mesh, h);
                var colors = new Doudou.Core.Color.Lab?[occ.Cells.Length];
                return Quantizer.Build(occ, colors, S.Lib, new QuantizeOptions { BrandId = S.Settings.DefaultBrand }, Path.GetFileNameWithoutExtension(file));
            });
            S.Replace(m, new ProjectMeta { Title = Path.GetFileNameWithoutExtension(file) }, new());
            Say($"网格已体素化：{m.SX}×{m.SY}×{m.SZ}，{m.CountSolid()} 颗（无颜色，请在编辑中上色）");
            GoStep(2);
        });
    }

    private void OnExportVox(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "MagicaVoxel (*.vox)|*.vox", FileName = S.Meta.Title + ".vox" };
        if (dlg.ShowDialog(this) != true) return;
        try { VoxFile.Write(S.Model, dlg.FileName); Say("已导出 " + dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => new SettingsWindow(S) { Owner = this }.ShowDialog();
    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        S.Hy3d.Dispose();
    }

    private void OnUndo(object sender, RoutedEventArgs e) { S.History.Undo(); }
    private void OnRedo(object sender, RoutedEventArgs e) { S.History.Redo(); }
    private void OnFrame(object sender, RoutedEventArgs e) => View.FrameModel();
    private void OnViewFront(object sender, RoutedEventArgs e) => View.SetView(0, 0);
    private void OnViewLeft(object sender, RoutedEventArgs e) => View.SetView(90, 0);
    private void OnViewTop(object sender, RoutedEventArgs e) => View.SetView(0, 89);
    private void OnViewIso(object sender, RoutedEventArgs e) => View.SetView(35, 28);

    private void OnHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "视口：右键拖动=旋转，中键拖动或空格+左键=平移，滚轮=缩放，F=居中，Alt+左键=吸色\n\n" +
        "动作：1 贴豆  2 擦除  3 上色  4 零件刷  5 选择\n" +
        "笔刷：V 单格  G 整面  B 矩形  L 直线  R 填充  P 平面\n" +
        "镜像：Shift+X / Shift+Y / Shift+Z\n" +
        "剖切：PageUp / PageDown 移动当前层\n" +
        "选择：Shift 加选，Ctrl 减选，双击清空；Delete 删除所选；Esc 取消\n" +
        "撤销/重做：Ctrl+Z / Ctrl+Y；保存：Ctrl+S",
        "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);

    // ───────────── 快捷键 ─────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var mod = Keyboard.Modifiers;
        if (mod == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: S.History.Undo(); e.Handled = true; return;
                case Key.Y: S.History.Redo(); e.Handled = true; return;
                case Key.S: Save(false); e.Handled = true; return;
                case Key.O: OnOpen(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.N: OnNew(this, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        if (mod == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z) { S.History.Redo(); e.Handled = true; return; }
        if (mod == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.X: S.MirrorX = !S.MirrorX; e.Handled = true; return;
                case Key.Y: S.MirrorY = !S.MirrorY; e.Handled = true; return;
                case Key.Z: S.MirrorZ = !S.MirrorZ; e.Handled = true; return;
            }
        }
        if (mod != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space: View.SetSpace(true); e.Handled = true; break;
            case Key.F: View.FrameModel(); break;
            case Key.D1: S.Action = EditAction.Attach; break;
            case Key.D2: S.Action = EditAction.Erase; break;
            case Key.D3: S.Action = EditAction.Paint; break;
            case Key.D4: S.Action = EditAction.PartPaint; break;
            case Key.D5: S.Action = EditAction.Select; break;
            case Key.V: S.Brush = BrushMode.Voxel; break;
            case Key.G: S.Brush = BrushMode.Face; break;
            case Key.B: S.Brush = BrushMode.Box; break;
            case Key.L: S.Brush = BrushMode.Line; break;
            case Key.R: S.Brush = BrushMode.Fill; break;
            case Key.P: S.Brush = BrushMode.Plane; break;
            case Key.Escape: View.Tools?.Cancel(); S.Selection.Clear(); S.RaiseChanged(); break;
            case Key.Delete: _edit.DeleteSelection(); break;
            case Key.PageUp: StepClip(1); e.Handled = true; break;
            case Key.PageDown: StepClip(-1); e.Handled = true; break;
        }
    }

    private void StepClip(int d)
    {
        if (S.ClipAxis < 0) { S.ClipAxis = 1; S.ClipMin = 0; S.ClipMax = 0; return; }
        int max = S.Model.Size(S.ClipAxis) - 1;
        int v = Math.Clamp(S.ClipMax + d, 0, max);
        S.ClipMax = v;
        if (S.ClipMin > v) S.ClipMin = v;
        Say($"剖切：显示 {"XYZ"[S.ClipAxis]} = {S.ClipMin}…{S.ClipMax}");
    }

    // ───────────── 拖放 ─────────────

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var ext = Path.GetExtension(files[0]).ToLowerInvariant();
        if (ext == ".doudou") { if (ConfirmDiscard()) OpenProject(files[0]); return; }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
        {
            GoStep(1);
            _import.AddImages(files);
        }
    }

    // ───────────── 图纸预览 ─────────────

    private double _zoom = 1;

    private void OnPreviewPick(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewPick.Tag is not IReadOnlyList<string> files || PreviewPick.SelectedIndex < 0) return;
        var f = files[PreviewPick.SelectedIndex];
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(f);
        // 超大图纸按 4000 宽解码，避免占用过多内存
        bmp.DecodePixelWidth = 4000;
        bmp.EndInit();
        PreviewImage.Source = bmp;
        _zoom = Math.Max(0.1, (PreviewScroll.ActualWidth - 30) / bmp.PixelWidth);
        PreviewImage.Width = bmp.PixelWidth * _zoom;
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || PreviewImage.Source is not BitmapSource b) return;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 0.87), 0.05, 4);
        PreviewImage.Width = b.PixelWidth * _zoom;
        e.Handled = true;
    }

    private void OnClosePreview(object sender, RoutedEventArgs e) => PreviewHost.Visibility = Visibility.Collapsed;
}
