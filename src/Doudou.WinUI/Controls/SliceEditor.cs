using System.Runtime.InteropServices.WindowsRuntime;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Doudou.Editor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI.Controls;

/// <summary>
/// 分层编辑器：像画拼豆图纸一样逐层编辑（与三维视图共用同一个模型，实时同步）。
/// 左键 = 当前工具；右键 / 中键拖动 = 平移；滚轮 = 缩放；PgUp / PgDn 或 [ ] = 换层。
/// 工具：画笔 / 橡皮 / 填充 / 吸色 / 矩形 / 零件笔 / 零件填充。
/// </summary>
public sealed class SliceEditor : Grid
{
    public enum Tool { Pen, Eraser, Fill, Pick, Rect, PartPen, PartFill }

    private Session? _s;
    private readonly Grid _surface = new() { Background = Solid(0x10, 0x15, 0x20) };
    private readonly Image _img = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private WriteableBitmap? _bmp;
    private byte[] _buf = Array.Empty<byte>();
    private readonly Slider _layerSlider = new() { Minimum = 0, StepFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _layerText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = Theme.F2, MinWidth = 70 };
    private readonly TextBlock _hover = new() { FontSize = Theme.F2, Foreground = Theme.B(Theme.Text2), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _axisBox;
    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
    private readonly ToggleButton _onion = TextToggle("透视上下层", "淡淡显示相邻两层，方便对齐", 84);
    private readonly ToggleButton _clip3d = TextToggle("三维只显示到本层", "三维视图只显示到当前层，像一层层搭起来", 118);

    public Tool Current { get; private set; } = Tool.Pen;

    private bool _editEnabled = true;
    private ScrollViewer _toolsScroll = null!;
    private readonly TextBlock _readOnlyLabel = new()
    {
        Text = "只读预览 · 回编辑页修改", FontSize = Theme.F2, Foreground = Theme.B(Theme.Text3),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S3, 0, Theme.S3, 0),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>只读时仍可平移/缩放/换层，但不会修改模型。</summary>
    public bool EditEnabled
    {
        get => _editEnabled;
        set
        {
            _editEnabled = value;
            if (_toolsScroll == null) return;
            _toolsScroll.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _readOnlyLabel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    /// <summary>切片轴：0=X（侧视）、1=Y（俯视，水平层，默认）、2=Z（正视）。</summary>
    public int Axis { get; private set; } = 1;
    public int Layer { get; private set; }

    // 视图：cell 像素大小与左上角偏移（逻辑像素）
    private double _cell = 16, _ox = 20, _oy = 20;
    private bool _fitted, _panning, _drawing, _sync;
    private Windows.Foundation.Point _panLast;
    private (int C, int R)? _rectStart, _lastCell;
    private (int C, int R) _rectEnd;
    private bool _redrawQueued;

    public event Action<string>? Message;

    public SliceEditor()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _axisBox = Combo(new[] { "水平层（俯视）", "前后层（正视）", "左右层（侧视）" }, 0, i =>
        {
            if (_sync) return;
            Axis = i switch { 1 => 2, 2 => 0, _ => 1 };
            _fitted = false;
            ClampLayer();
            SyncClip();
            QueueRedraw();
        });
        _axisBox.Width = 140;
        _layerSlider.ValueChanged += (_, e) => { if (!_sync) SetLayer((int)e.NewValue); };
        var prev = IconBtn(G.Back, "下一层（PgDn / [）", () => SetLayer(Layer - 1));
        var next = IconBtn(G.Play, "上一层（PgUp / ]）", () => SetLayer(Layer + 1));
        var fit = IconBtn(G.Fit, "缩放到整层可见（双击格子外的空白处也可以）", () => { _fitted = false; Fit(); QueueRedraw(); });

        var bar = new Grid { ColumnSpacing = Theme.S2, Padding = new Thickness(Theme.S2), Background = Theme.B(Theme.Surface2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_axisBox);
        var lt = HStack(prev, _layerText, next, fit);
        Grid.SetColumn(lt, 1); bar.Children.Add(lt);
        Grid.SetColumn(_layerSlider, 2); bar.Children.Add(_layerSlider);
        Grid.SetColumn(_onion, 3); bar.Children.Add(_onion);
        Grid.SetColumn(_clip3d, 4); bar.Children.Add(_clip3d);
        _onion.IsChecked = true;
        _onion.Click += (_, _) => QueueRedraw();
        _clip3d.Click += (_, _) => SyncClip();
        Children.Add(bar);

        _surface.Children.Add(_img);
        _surface.SizeChanged += (_, _) => { if (!_fitted) Fit(); QueueRedraw(); };
        _surface.PointerPressed += OnPressed;
        _surface.PointerMoved += OnMoved;
        _surface.PointerReleased += OnReleased;
        _surface.PointerWheelChanged += OnWheel;
        _surface.PointerExited += (_, _) => { _hover.Text = ""; };
        _surface.PointerCaptureLost += (_, _) => { _panning = false; if (_drawing) EndStroke(); };
        // 只有双击在格子外面才复位：双击豆子时常常是想快速连点，不能把用户的缩放平移重置掉
        _surface.DoubleTapped += (_, e) =>
        {
            if (HitCell(e.GetPosition(_surface)) != null) return;
            _fitted = false; Fit(); QueueRedraw();
        };
        Grid.SetRow(_surface, 1);
        Children.Add(_surface);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S1, Padding = new Thickness(Theme.S2), Background = Theme.B(Theme.Surface2) };
        foreach (var (t, glyph, label, tip) in new[]
                 {
                     (Tool.Pen, G.Edit, "画笔", "点击/拖动：放豆（已有豆则改成当前色）"),
                     (Tool.Eraser, G.Erase, "橡皮", "点击/拖动：删除豆"),
                     (Tool.Fill, G.Paint, "填充", "同一层里连着的同色区域换成当前色（点空格 = 填满空白区域）"),
                     (Tool.Pick, G.Pick, "吸色", "吸取颜色和零件（任何工具下 Alt+点击 也可以）"),
                     (Tool.Rect, G.Crop, "矩形", "拖出矩形：填充当前色；按住 Ctrl 拖 = 清空"),
                     (Tool.PartPen, G.Part, "零件笔", "把豆分给当前零件（拆件用）"),
                     (Tool.PartFill, G.Merge, "零件填充", "同一层里连着的同零件区域整块分给当前零件"),
                 })
        {
            var b = TextToggle(label, tip, 64);
            b.Content = Label(label, glyph);
            b.Width = double.NaN; b.Padding = new Thickness(Theme.S2, 0, Theme.S2, 0);
            b.Click += (_, _) => SetTool(t);
            _toolButtons[t] = b;
            tools.Children.Add(b);
        }
        var foot = new Grid { Background = Theme.B(Theme.Surface2) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        _toolsScroll = new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        foot.Children.Add(_toolsScroll);
        foot.Children.Add(_readOnlyLabel);
        _hover.Margin = new Thickness(Theme.S3, 0, Theme.S3, 0);
        _hover.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_hover, 1);
        foot.Children.Add(_hover);
        Grid.SetRow(foot, 2);
        Children.Add(foot);
        SetTool(Tool.Pen);
    }

    public void Attach(Session s)
    {
        _s = s;
        s.ModelChanged += () => DispatcherQueue.TryEnqueue(() => { ClampLayer(); QueueRedraw(); });
        s.ModelReplaced += () => DispatcherQueue.TryEnqueue(() => { _fitted = false; Layer = 0; ClampLayer(); SyncClip(); QueueRedraw(); });
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Session.CurrentColor) or nameof(Session.CurrentPart) or nameof(Session.Display)) QueueRedraw();
        };
        ClampLayer();
    }

    /// <summary>分层视图显示/隐藏时调用：隐藏时恢复三维的完整显示。</summary>
    public void SetActive(bool on)
    {
        if (_s == null) return;
        if (on) { ClampLayer(); SyncClip(); QueueRedraw(); }
        else if (_clip3d.IsChecked == true) _s.ClipAxis = -1;
    }

    public void SetTool(Tool t)
    {
        Current = t;
        foreach (var (k, b) in _toolButtons) b.IsChecked = k == t;
        if (_s != null && t is Tool.PartPen or Tool.PartFill && _s.Display != DisplayMode.PartTint) _s.Display = DisplayMode.PartTint;
        QueueRedraw();
    }

    // ───────────── 坐标映射：二维格 (列 c, 行 r) ↔ 三维 ─────────────
    // 俯视（Y）：列 = X，行 = Z（上方是背面）；正视（Z）：列 = X，行 = 自上而下的 Y；侧视（X）：列 = Z，行 = 自上而下的 Y

    private VoxelModel M => _s!.Model;
    private int LayerCount => _s == null ? 1 : M.Size(Axis);
    private (int Cols, int Rows) Dims => Axis switch { 1 => (M.SX, M.SZ), 2 => (M.SX, M.SY), _ => (M.SZ, M.SY) };

    private Int3 ToCell(int c, int r) => Axis switch
    {
        1 => new Int3(c, Layer, r),
        2 => new Int3(c, M.SY - 1 - r, Layer),
        _ => new Int3(Layer, M.SY - 1 - r, M.SZ - 1 - c),
    };

    private Int3 ToCellAt(int c, int r, int layer) => Axis switch
    {
        1 => new Int3(c, layer, r),
        2 => new Int3(c, M.SY - 1 - r, layer),
        _ => new Int3(layer, M.SY - 1 - r, M.SZ - 1 - c),
    };

    private void ClampLayer()
    {
        if (_s == null) return;
        _sync = true;
        _layerSlider.Maximum = Math.Max(1, LayerCount - 1);
        Layer = Math.Clamp(Layer, 0, LayerCount - 1);
        _layerSlider.Value = Layer;
        _layerText.Text = $"第 {Layer + 1}/{LayerCount} 层";
        _sync = false;
    }

    public void SetLayer(int layer)
    {
        if (_s == null) return;
        Layer = Math.Clamp(layer, 0, LayerCount - 1);
        ClampLayer();
        SyncClip();
        QueueRedraw();
    }

    private void SyncClip()
    {
        if (_s == null || Visibility != Visibility.Visible) return;
        if (_clip3d.IsChecked == true) { _s.ClipAxis = Axis; _s.ClipMin = 0; _s.ClipMax = Layer; }
        else if (_s.ClipAxis == Axis) _s.ClipAxis = -1;
    }

    private void Fit()
    {
        if (_s == null || _surface.ActualWidth < 10) return;
        var (cols, rows) = Dims;
        _cell = Math.Clamp(Math.Min((_surface.ActualWidth - 40) / cols, (_surface.ActualHeight - 40) / rows), 2, 64);
        _ox = (_surface.ActualWidth - cols * _cell) / 2;
        _oy = (_surface.ActualHeight - rows * _cell) / 2;
        _fitted = true;
    }

    private (int C, int R)? HitCell(Windows.Foundation.Point p)
    {
        var (cols, rows) = Dims;
        int c = (int)Math.Floor((p.X - _ox) / _cell), r = (int)Math.Floor((p.Y - _oy) / _cell);
        return c >= 0 && r >= 0 && c < cols && r < rows ? (c, r) : null;
    }

    // ───────────── 绘制（CPU 位图，按物理像素，网格清晰不糊）─────────────

    public void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _redrawQueued = false; Redraw(); });
    }

    private void Redraw()
    {
        if (_s == null || _surface.ActualWidth < 2 || Visibility != Visibility.Visible) return;
        if (!_fitted) Fit();
        double scale = XamlRoot?.RasterizationScale ?? 1;
        int W = Math.Max(1, (int)(_surface.ActualWidth * scale)), H = Math.Max(1, (int)(_surface.ActualHeight * scale));
        if (_bmp == null || _bmp.PixelWidth != W || _bmp.PixelHeight != H)
        {
            _bmp = new WriteableBitmap(W, H);
            _buf = new byte[W * H * 4];
            _img.Source = _bmp;
        }
        _img.Width = _surface.ActualWidth; _img.Height = _surface.ActualHeight;
        Fill(0x10, 0x15, 0x20);
        var m = M;
        var (cols, rows) = Dims;
        double cs = _cell * scale, ox = _ox * scale, oy = _oy * scale;
        bool onion = _onion.IsChecked == true;
        bool tint = _s.Display == DisplayMode.PartTint;
        int gap = cs >= 6 ? 1 : 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x0 = (int)(ox + c * cs), y0 = (int)(oy + r * cs), x1 = (int)(ox + (c + 1) * cs), y1 = (int)(oy + (r + 1) * cs);
                if (x1 < 0 || y1 < 0 || x0 >= W || y0 >= H) continue;
                var p = ToCell(c, r);
                int i = m.Index(p);
                ushort col = m.Colors[i];
                if (col != 0)
                {
                    var bc = m.Palette[col];
                    Rgb rgb = bc.IsAny ? new Rgb(64, 74, 70) : bc.Rgb;
                    if (tint)
                    {
                        var part = m.Parts[i] != 0 ? m.FindPart(m.Parts[i]) : null;
                        rgb = part?.Tint ?? new Rgb(120, 124, 130);
                        if (m.Parts[i] == _s.CurrentPart && m.Parts[i] != 0) rgb = Rgb.Lerp(rgb, new Rgb(255, 255, 255), 0.3);
                    }
                    if (_s.Highlight.Contains(i)) rgb = Rgb.Lerp(rgb, new Rgb(255, 40, 30), 0.6);
                    Rect(x0, y0, x1 - gap, y1 - gap, rgb.R, rgb.G, rgb.B);
                    // 豆孔
                    if (cs >= 10 && !tint)
                    {
                        var d = rgb.Scale(0.6);
                        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rr = Math.Max(1, (int)(cs * 0.13));
                        Rect(cx - rr, cy - rr, cx + rr, cy + rr, d.R, d.G, d.B);
                    }
                    if (tint && m.Parts[i] == 0 && cs >= 6) Rect(x0 + 2, y0 + 2, x0 + (int)(cs / 3), y0 + (int)(cs / 3), 255, 60, 60);
                }
                else
                {
                    // 空格：网格 + 透视相邻层
                    byte g = (byte)(((c / 5) + (r / 5)) % 2 == 0 ? 0x1A : 0x1E);
                    Rect(x0, y0, x1 - gap, y1 - gap, g, (byte)(g + 4), (byte)(g + 10));
                    if (onion)
                    {
                        foreach (var (dl, a) in new[] { (-1, 0.45), (1, 0.22) })
                        {
                            int L = Layer + dl;
                            if (L < 0 || L >= LayerCount) continue;
                            var q = ToCellAt(c, r, L);
                            var qc = m.Colors[m.Index(q)];
                            if (qc == 0) continue;
                            var rgb = Rgb.Lerp(new Rgb(g, (byte)(g + 4), (byte)(g + 10)), m.Palette[qc].Rgb, a);
                            int inset = Math.Max(1, (int)(cs * 0.22));
                            Rect(x0 + inset, y0 + inset, x1 - gap - inset, y1 - gap - inset, rgb.R, rgb.G, rgb.B);
                            break;
                        }
                    }
                }
            }
        // 每 5 格加粗的参考线（与图纸一致）
        if (cs >= 4)
            for (int c = 0; c <= cols; c += 5) { int x = (int)(ox + c * cs); Rect(x - 1, (int)oy, x, (int)(oy + rows * cs), 0x46, 0x55, 0x6E); }
        if (cs >= 4)
            for (int r = 0; r <= rows; r += 5) { int y = (int)(oy + r * cs); Rect((int)ox, y - 1, (int)(ox + cols * cs), y, 0x46, 0x55, 0x6E); }
        // 矩形预览
        if (_rectStart is { } a2 && Current == Tool.Rect)
        {
            int c0 = Math.Min(a2.C, _rectEnd.C), c1 = Math.Max(a2.C, _rectEnd.C), r0 = Math.Min(a2.R, _rectEnd.R), r1 = Math.Max(a2.R, _rectEnd.R);
            int x0 = (int)(ox + c0 * cs), y0 = (int)(oy + r0 * cs), x1 = (int)(ox + (c1 + 1) * cs), y1 = (int)(oy + (r1 + 1) * cs);
            Rect(x0, y0, x1, y0 + 2, 0xF9, 0x9E, 0x1A); Rect(x0, y1 - 2, x1, y1, 0xF9, 0x9E, 0x1A);
            Rect(x0, y0, x0 + 2, y1, 0xF9, 0x9E, 0x1A); Rect(x1 - 2, y0, x1, y1, 0xF9, 0x9E, 0x1A);
        }
        using (var st = _bmp.PixelBuffer.AsStream()) st.Write(_buf, 0, _buf.Length);
        _bmp.Invalidate();

        void Fill(byte r, byte g, byte b)
        {
            for (int k = 0; k < _buf.Length; k += 4) { _buf[k] = b; _buf[k + 1] = g; _buf[k + 2] = r; _buf[k + 3] = 255; }
        }
        void Rect(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(W, x1); y1 = Math.Min(H, y1);
            for (int y = y0; y < y1; y++)
            {
                int o = (y * W + x0) * 4;
                for (int x = x0; x < x1; x++, o += 4) { _buf[o] = b; _buf[o + 1] = g; _buf[o + 2] = r; _buf[o + 3] = 255; }
            }
        }
    }

    // ───────────── 输入 ─────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_s == null) return;
        var pt = e.GetCurrentPoint(_surface);
        _surface.CapturePointer(e.Pointer);
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed) { _panning = true; _panLast = pt.Position; return; }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (!EditEnabled) return;
        if (_s.Explode > 1.01f) { Message?.Invoke("爆炸预览中不能编辑，请先把爆炸系数调回 1"); return; }
        var hit = HitCell(pt.Position);
        if (hit == null) return;
        bool alt = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu);
        if (alt || Current == Tool.Pick) { PickAt(hit.Value); return; }
        if (Current == Tool.Rect) { _rectStart = hit; _rectEnd = hit.Value; _drawing = true; QueueRedraw(); return; }
        if (Current is Tool.Fill or Tool.PartFill) { FloodAt(hit.Value); return; }
        _s.History.Begin(Current switch { Tool.Eraser => "分层：擦除", Tool.PartPen => "分层：分配零件", _ => "分层：画豆" });
        _drawing = true;
        _lastCell = null;
        StrokeTo(hit.Value);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_s == null) return;
        var p = e.GetCurrentPoint(_surface).Position;
        if (_panning)
        {
            _ox += p.X - _panLast.X; _oy += p.Y - _panLast.Y; _panLast = p;
            QueueRedraw();
            return;
        }
        var hit = HitCell(p);
        if (hit is { } h)
        {
            var cell = ToCell(h.C, h.R);
            int i = M.Index(cell);
            var part = M.Parts[i] != 0 ? M.FindPart(M.Parts[i]) : null;
            _hover.Text = $"列 {h.C + 1} 行 {h.R + 1}  ·  {(M.Colors[i] == 0 ? "空" : M.Palette[M.Colors[i]].ToString())}{(part != null ? "  ·  " + part.Name : "")}";
        }
        if (!EditEnabled) return;
        if (!_drawing || hit == null) return;
        if (Current == Tool.Rect) { _rectEnd = hit.Value; QueueRedraw(); return; }
        StrokeTo(hit.Value);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _surface.ReleasePointerCapture(e.Pointer);
        _panning = false;
        if (!_drawing) return;
        if (Current == Tool.Rect && _rectStart is { } a)
        {
            bool clear = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);
            ApplyRect(a, _rectEnd, clear);
            _rectStart = null;
            _drawing = false;
            QueueRedraw();
            return;
        }
        EndStroke();
    }

    private void EndStroke()
    {
        _drawing = false;
        _lastCell = null;
        if (_s != null && _s.History.InTransaction) _s.History.Commit();
        QueueRedraw();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_surface);
        double f = pt.Properties.MouseWheelDelta > 0 ? 1.15 : 1 / 1.15;
        double nc = Math.Clamp(_cell * f, 2, 96);
        f = nc / _cell;
        _ox = pt.Position.X - (pt.Position.X - _ox) * f;
        _oy = pt.Position.Y - (pt.Position.Y - _oy) * f;
        _cell = nc;
        QueueRedraw();
        e.Handled = true;
    }

    /// <summary>沿两点之间的直线逐格画（拖得快也不断线）。</summary>
    private void StrokeTo((int C, int R) to)
    {
        var from = _lastCell ?? to;
        int n = Math.Max(Math.Abs(to.C - from.C), Math.Abs(to.R - from.R));
        for (int k = 0; k <= n; k++)
        {
            int c = n == 0 ? to.C : from.C + (int)Math.Round((to.C - from.C) * (double)k / n);
            int r = n == 0 ? to.R : from.R + (int)Math.Round((to.R - from.R) * (double)k / n);
            ApplyCell(c, r);
        }
        _lastCell = to;
        QueueRedraw();
    }

    private IEnumerable<Int3> Mirrored(Int3 p) => Doudou.Core.Edit.Brushes.Mirrored(M, new[] { p }, _s!.MirrorX, _s.MirrorY, _s.MirrorZ);

    private void ApplyCell(int c, int r)
    {
        var s = _s!;
        foreach (var p in Mirrored(ToCell(c, r)))
        {
            if (!M.InBounds(p)) continue;
            int i = M.Index(p);
            switch (Current)
            {
                case Tool.Pen:
                    var col = s.CurrentColor == 0 ? (ushort)1 : s.CurrentColor;
                    if (M.Colors[i] == 0) s.History.Set(i, col, 0);
                    else if (M.Colors[i] != col) s.History.SetColor(i, col);
                    break;
                case Tool.Eraser:
                    if (M.Colors[i] != 0) s.History.Set(i, 0, 0);
                    break;
                case Tool.PartPen:
                    if (M.Colors[i] == 0) break;
                    EnsurePart();
                    s.History.SetPart(i, (ushort)s.CurrentPart);
                    break;
            }
        }
    }

    private void EnsurePart()
    {
        if (_s!.CurrentPart != 0 && M.FindPart(_s.CurrentPart) != null) return;
        var p = M.NewPart();
        _s.CurrentPart = p.Id;
        Message?.Invoke($"已新建零件 {p.Name}");
    }

    private void ApplyRect((int C, int R) a, (int C, int R) b, bool clear)
    {
        var s = _s!;
        s.History.Begin(clear ? "分层：矩形清空" : "分层：矩形填充");
        var col = s.CurrentColor == 0 ? (ushort)1 : s.CurrentColor;
        for (int r = Math.Min(a.R, b.R); r <= Math.Max(a.R, b.R); r++)
            for (int c = Math.Min(a.C, b.C); c <= Math.Max(a.C, b.C); c++)
                foreach (var p in Mirrored(ToCell(c, r)))
                {
                    if (!M.InBounds(p)) continue;
                    int i = M.Index(p);
                    if (clear) { if (M.Colors[i] != 0) s.History.Set(i, 0, 0); }
                    else if (M.Colors[i] == 0) s.History.Set(i, col, 0);
                    else if (M.Colors[i] != col) s.History.SetColor(i, col);
                }
        s.History.Commit();
    }

    /// <summary>层内 4 邻域填充：颜色填充按「同色」扩散（含空格）；零件填充按「同零件」扩散（只走有豆的格）。</summary>
    private void FloodAt((int C, int R) start)
    {
        var s = _s!;
        var (cols, rows) = Dims;
        var p0 = ToCell(start.C, start.R);
        int i0 = M.Index(p0);
        bool part = Current == Tool.PartFill;
        if (part && M.Colors[i0] == 0) return;
        ushort matchC = M.Colors[i0], matchP = M.Parts[i0];
        var col = s.CurrentColor == 0 ? (ushort)1 : s.CurrentColor;
        if (!part && matchC == col) return;
        if (part) EnsurePart();
        if (part && matchP == s.CurrentPart) return;
        s.History.Begin(part ? "分层：零件填充" : "分层：填充");
        var seen = new bool[cols * rows];
        var q = new Queue<(int, int)>();
        q.Enqueue(start); seen[start.R * cols + start.C] = true;
        int n = 0;
        while (q.Count > 0 && n < 200000)
        {
            var (c, r) = q.Dequeue(); n++;
            int i = M.Index(ToCell(c, r));
            if (part) s.History.SetPart(i, (ushort)s.CurrentPart);
            else if (M.Colors[i] == 0) s.History.Set(i, col, 0);
            else s.History.SetColor(i, col);
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nc = c + dc, nr = r + dr;
                if (nc < 0 || nr < 0 || nc >= cols || nr >= rows || seen[nr * cols + nc]) continue;
                int j = M.Index(ToCell(nc, nr));
                bool ok = part ? M.Colors[j] != 0 && M.Parts[j] == matchP : M.Colors[j] == matchC;
                if (!ok) continue;
                seen[nr * cols + nc] = true;
                q.Enqueue((nc, nr));
            }
        }
        s.History.Commit();
        QueueRedraw();
    }

    private void PickAt((int C, int R) h)
    {
        int i = M.Index(ToCell(h.C, h.R));
        if (M.Colors[i] == 0) return;
        _s!.CurrentColor = M.Colors[i];
        if (M.Parts[i] != 0) _s.CurrentPart = M.Parts[i];
        _s.Notify(nameof(Session.CurrentColor));
        Message?.Invoke($"已吸取 {M.Palette[M.Colors[i]]}" + (M.Parts[i] != 0 ? $"，零件 {M.FindPart(M.Parts[i])?.Name}" : ""));
    }
}
