using Doudou.Core.Color;
using Doudou.Core.Imaging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Controls;

/// <summary>
/// 主体框选对话框：左键拖动框选主体；滚轮以鼠标为中心缩放；右键 / 中键拖动平移；双击复位。
/// 「吸色」模式下点击图片把该处颜色加入主色。
/// </summary>
public sealed class SubjectDialog
{
    public (int X0, int Y0, int X1, int Y1)? Box { get; private set; }
    public List<Rgb> PickedColors { get; } = new();
    public bool SkipRest { get; private set; }

    private readonly string _path;
    private readonly RgbaImage _img;
    private readonly Grid _canvas = new() { Background = Solid(0x0E, 0x12, 0x1A) };
    // 必须左上对齐：放在 Grid 里默认会被居中，坐标换算就会错位
    private readonly Canvas _layer = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Rectangle _rect = new() { Stroke = Theme.B(Theme.Orange), Fill = Theme.B(Theme.Orange, 0x22), Visibility = Visibility.Collapsed };
    private readonly CompositeTransform _xf = new();
    private readonly TextBlock _info = new() { FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _pick = new() { Content = Label("吸色", G.Pick) };
    private readonly ItemsWrap _swatches = new() { HSpacing = 4, VSpacing = 4 };
    private Windows.Foundation.Point? _dragStart;
    private Windows.Foundation.Point _panLast;
    private bool _panning;
    private bool _fitted;

    public SubjectDialog(string path, (int, int, int, int)? existing, IEnumerable<Rgb>? colors = null)
    {
        _path = path;
        _img = RgbaImage.Load(path);
        Box = existing;
        if (colors != null) PickedColors.AddRange(colors);
    }

    public async Task<ContentDialogResult> ShowAsync(XamlRoot root, int index, int total)
    {
        // 直接用算法读到的像素（已按 EXIF 旋转）生成位图，显示与框选坐标严格一致
        _image.Source = ToBitmap(_img);
        _image.Width = _img.W; _image.Height = _img.H;
        _layer.Width = _img.W; _layer.Height = _img.H;
        _layer.RenderTransform = _xf;
        _layer.Children.Add(_image);
        _layer.Children.Add(_rect);
        _canvas.Children.Add(_layer);
        _canvas.Clip = new RectangleGeometry();
        _canvas.SizeChanged += (_, e) =>
        {
            ((RectangleGeometry)_canvas.Clip).Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            if (!_fitted && e.NewSize.Width > 10) { Fit(); _fitted = true; }
        };
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerWheelChanged += OnWheel;
        _canvas.DoubleTapped += (_, _) => Fit();
        UpdateRect();
        RefreshSwatches();

        double w = Math.Max(600, root.Size.Width * 0.82), h = Math.Max(480, root.Size.Height * 0.78);
        var top = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = "左键拖动：框选主体　滚轮：缩放　右键/中键拖动：平移　双击：复位" + (total > 1 ? $"　（第 {index}/{total} 张）" : ""),
            Opacity = 0.75, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tools.Children.Add(_pick);
        var clear = new Button { Content = "清除框" };
        clear.Click += (_, _) => { Box = null; UpdateRect(); };
        tools.Children.Add(clear);
        Grid.SetColumn(tools, 1);
        top.Children.Add(tools);
        ToolTipService.SetToolTip(_pick, "打开后点击图片，把该处颜色加入「主色」（取 5×5 区域中位色）");

        var bottom = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.Children.Add(_info);
        var sw = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        sw.Children.Add(new TextBlock { Text = "主色：", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        sw.Children.Add(_swatches);
        Grid.SetColumn(sw, 1);
        bottom.Children.Add(sw);

        var body = new Grid { Width = w, Height = h };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(top);
        var frame = new Border { Child = _canvas, CornerRadius = new CornerRadius(6), BorderBrush = Theme.B(Theme.Orange, 0x60), BorderThickness = new Thickness(1) };
        Grid.SetRow(frame, 1);
        body.Children.Add(frame);
        Grid.SetRow(bottom, 2);
        body.Children.Add(bottom);

        var dlg = new ContentDialog
        {
            Title = "框选主体 · " + Path.GetFileName(_path),
            Content = body,
            PrimaryButtonText = "确定",
            SecondaryButtonText = total > 1 && index < total ? "其余都用整张" : "使用整张图",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        dlg.Resources["ContentDialogMaxWidth"] = w + 80;
        dlg.Resources["ContentDialogMaxHeight"] = h + 220;
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.Secondary)
        {
            if (total > 1 && index < total) SkipRest = true;
            else Box = null;
        }
        return r;
    }

    /// <summary>RGBA → WriteableBitmap（BGRA 预乘）。超大照片按比例缩小显示，但图片控件仍按原图像素尺寸摆放。</summary>
    public static WriteableBitmap ToBitmap(RgbaImage img)
    {
        var src = img;
        int longSide = Math.Max(img.W, img.H);
        if (longSide > 2600) { double k = 2600.0 / longSide; src = img.Resized(Math.Max(1, (int)(img.W * k)), Math.Max(1, (int)(img.H * k))); }
        var bmp = new WriteableBitmap(src.W, src.H);
        var buf = new byte[src.W * src.H * 4];
        for (int i = 0; i < src.W * src.H; i++)
        {
            byte a = src.Px[i * 4 + 3];
            buf[i * 4] = (byte)(src.Px[i * 4 + 2] * a / 255);
            buf[i * 4 + 1] = (byte)(src.Px[i * 4 + 1] * a / 255);
            buf[i * 4 + 2] = (byte)(src.Px[i * 4] * a / 255);
            buf[i * 4 + 3] = a;
        }
        using (var st = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bmp.PixelBuffer)) st.Write(buf, 0, buf.Length);
        bmp.Invalidate();
        return bmp;
    }

    // ───────────── 坐标 ─────────────

    private Windows.Foundation.Point ToImage(Windows.Foundation.Point p) =>
        new((p.X - _xf.TranslateX) / _xf.ScaleX, (p.Y - _xf.TranslateY) / _xf.ScaleY);

    private void Fit()
    {
        double cw = Math.Max(1, _canvas.ActualWidth), ch = Math.Max(1, _canvas.ActualHeight);
        double s = Math.Min(cw / _img.W, ch / _img.H) * 0.96;
        _xf.ScaleX = _xf.ScaleY = s;
        _xf.TranslateX = (cw - _img.W * s) / 2;
        _xf.TranslateY = (ch - _img.H * s) / 2;
        UpdateRect();
    }

    private void UpdateRect()
    {
        double sw = 2 / Math.Max(0.01, _xf.ScaleX);
        _rect.StrokeThickness = sw;
        _rect.StrokeDashArray = new DoubleCollection { 4, 3 };
        if (Box is { } b)
        {
            _rect.Visibility = Visibility.Visible;
            Canvas.SetLeft(_rect, b.X0); Canvas.SetTop(_rect, b.Y0);
            _rect.Width = Math.Max(1, b.X1 - b.X0); _rect.Height = Math.Max(1, b.Y1 - b.Y0);
            _info.Text = $"主体 {b.X1 - b.X0} × {b.Y1 - b.Y0} px（原图 {_img.W} × {_img.H}）";
        }
        else
        {
            _rect.Visibility = Visibility.Collapsed;
            _info.Text = $"未框选：使用整张图（{_img.W} × {_img.H}）";
        }
    }

    // ───────────── 输入 ─────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        _canvas.CapturePointer(e.Pointer);
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            _panning = true; _panLast = pt.Position; return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        var ip = ToImage(pt.Position);
        if (_pick.IsChecked == true)
        {
            PickAt((int)ip.X, (int)ip.Y);
            return;
        }
        _dragStart = new Windows.Foundation.Point(Math.Clamp(ip.X, 0, _img.W), Math.Clamp(ip.Y, 0, _img.H));
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        if (_panning)
        {
            _xf.TranslateX += p.X - _panLast.X;
            _xf.TranslateY += p.Y - _panLast.Y;
            _panLast = p;
            return;
        }
        if (_dragStart is not { } a) return;
        var ip = ToImage(p);
        double x = Math.Clamp(ip.X, 0, _img.W), y = Math.Clamp(ip.Y, 0, _img.H);
        Box = ((int)Math.Min(a.X, x), (int)Math.Min(a.Y, y), (int)Math.Max(a.X, x), (int)Math.Max(a.Y, y));
        UpdateRect();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        _panning = false;
        if (_dragStart != null && Box is { } b && (b.X1 - b.X0 < 8 || b.Y1 - b.Y0 < 8)) Box = null;   // 误点
        _dragStart = null;
        UpdateRect();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        double f = pt.Properties.MouseWheelDelta > 0 ? 1.15 : 1 / 1.15;
        double ns = Math.Clamp(_xf.ScaleX * f, 0.05, 20);
        f = ns / _xf.ScaleX;
        // 以鼠标位置为中心缩放
        _xf.TranslateX = pt.Position.X - (pt.Position.X - _xf.TranslateX) * f;
        _xf.TranslateY = pt.Position.Y - (pt.Position.Y - _xf.TranslateY) * f;
        _xf.ScaleX = _xf.ScaleY = ns;
        UpdateRect();
        e.Handled = true;
    }

    private void PickAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _img.W || y >= _img.H) return;
        var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int px = Math.Clamp(x + dx, 0, _img.W - 1), py = Math.Clamp(y + dy, 0, _img.H - 1);
                var c = _img.GetRgb(px, py);
                rs.Add(c.R); gs.Add(c.G); bs.Add(c.B);
            }
        rs.Sort(); gs.Sort(); bs.Sort();
        var col = new Rgb(rs[12], gs[12], bs[12]);
        if (PickedColors.Any(p => ColorMath.DeltaE(p, col) < 4)) return;
        PickedColors.Add(col);
        RefreshSwatches();
    }

    private void RefreshSwatches()
    {
        _swatches.Children.Clear();
        foreach (var c in PickedColors.ToList())
        {
            var b = new Button { Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0), Content = Swatch(c, 24) };
            ToolTipService.SetToolTip(b, c.Hex + "（点击移除）");
            b.Click += (_, _) => { PickedColors.Remove(c); RefreshSwatches(); };
            _swatches.Children.Add(b);
        }
        if (PickedColors.Count == 0) _swatches.Children.Add(new TextBlock { Text = "（打开「吸色」后点击图片添加）", Opacity = 0.55, FontSize = 12 });
    }
}
