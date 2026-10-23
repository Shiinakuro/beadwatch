using System.Numerics;
using System.Runtime.InteropServices;
using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Editor;
using Doudou.WinUI.Gfx;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Doudou.WinUI.Controls;

/// <summary>
/// 三维编辑视口（Direct3D 11 + SwapChainPanel）。操作仿 MagicaVoxel：
/// 左键 = 当前工具；右键拖 = 旋转；中键拖 / 空格+左键 = 平移；滚轮 = 缩放；Alt+左键 = 吸色。
/// </summary>
public sealed class VoxelView : Grid
{
    private readonly SwapChainPanel _panel = new();
    private readonly D3DRenderer _r = new();
    private Session? _s;
    public ToolController? Tools { get; private set; }

    private bool _meshDirty = true, _previewDirty = true, _linesDirty = true, _frameQueued;
    private bool _initFailed;
    private int _solid;
    private string _statusText = "";

    // 相机（单位：颗）
    private Vector3 _target;
    private float _yaw = 0.6f, _pitch = 0.45f, _dist = 80;
    private Matrix4x4 _view, _proj;

    private Windows.Foundation.Point _last;
    private bool _rotating, _panning, _spaceDown;

    /// <summary>悬停信息、尺寸统计变化时触发（状态栏用）。</summary>
    public event Action<string, string?>? StatusChanged;
    public event Action<string>? Message;

    public VoxelView()
    {
        Children.Add(_panel);
        _panel.Loaded += (_, _) => EnsureInit();
        _panel.SizeChanged += (_, _) => { ResizeSwapChain(); Invalidate(); };
        _panel.CompositionScaleChanged += (_, _) => { ResizeSwapChain(); Invalidate(); };
        // 指针事件挂在外层（透明背景保证可命中），坐标仍按交换链面板计算
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerWheelChanged += OnWheel;
        PointerExited += (_, _) => { Tools?.Preview.Clear(); Tools?.ClearHover(); _previewDirty = true; StatusChanged?.Invoke(_statusText, null); Invalidate(); };
        PointerCaptureLost += (_, _) => { _rotating = _panning = false; };
        Unloaded += (_, _) => _r.Dispose();
        IsTabStop = true;
    }

    public void Attach(Session s)
    {
        _s = s;
        Tools = new ToolController(s);
        s.ModelChanged += () => { _meshDirty = _linesDirty = true; Invalidate(); };
        s.RenderChanged += Invalidate;   // 只换渲染参数：重画一帧，不重建网格
        s.ModelReplaced += () => { FrameModel(); _meshDirty = _linesDirty = true; Invalidate(); };
        FrameModel();
    }

    // ───────────── D3D 初始化与交换链 ─────────────

    [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig] int SetSwapChain(IntPtr swapChain);
    }

    private (int W, int H) PixelSize() =>
        ((int)Math.Max(1, _panel.ActualWidth * _panel.CompositionScaleX), (int)Math.Max(1, _panel.ActualHeight * _panel.CompositionScaleY));

    private unsafe void EnsureInit()
    {
        if (_r.Ready || _initFailed) return;
        try
        {
            var (w, h) = PixelSize();
            var swap = _r.Initialize(w, h);
            // WinUI 3：从 WinRT 对象拿 ISwapChainPanelNative 再设置交换链
            var unk = ((WinRT.IWinRTObject)_panel).NativeObject.ThisPtr;
            var iid = typeof(ISwapChainPanelNative).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var native));
            try
            {
                var vtbl = *(IntPtr**)native;
                var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[3];
                Marshal.ThrowExceptionForHR(setSwapChain(native, swap));
            }
            finally { Marshal.Release(native); }
            _r.Resize(w, h, _panel.CompositionScaleX, _panel.CompositionScaleY);
            Invalidate();
        }
        catch (Exception e)
        {
            _initFailed = true;
            Message?.Invoke("三维视口初始化失败（显卡驱动不支持 Direct3D 11？）：" + e.Message);
        }
    }

    private void ResizeSwapChain()
    {
        if (!_r.Ready) return;
        var (w, h) = PixelSize();
        _r.Resize(w, h, _panel.CompositionScaleX, _panel.CompositionScaleY);
    }

    /// <summary>请求在下一帧重绘（按需渲染，不空转）。</summary>
    public void Invalidate()
    {
        if (_frameQueued) return;
        _frameQueued = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _frameQueued = false;
        RenderNow();
    }

    // ───────────── 相机 ─────────────

    public void FrameModel()
    {
        if (_s == null) return;
        var m = _s.Model;
        var b = m.Bounds();
        if (b is { } bb)
        {
            _target = new Vector3((bb.Min.X + bb.Max.X + 1) / 2f, (bb.Min.Y + bb.Max.Y + 1) / 2f, (bb.Min.Z + bb.Max.Z + 1) / 2f);
            var ext = bb.Max - bb.Min;
            _dist = Math.Max(20, Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) * 2.3f);
        }
        else
        {
            _target = new Vector3(m.SX / 2f, m.SY / 4f, m.SZ / 2f);
            _dist = Math.Max(m.SX, m.SZ) * 2.2f;
        }
        Invalidate();
    }

    public void FocusCell(Int3 c)
    {
        _target = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
        _dist = Math.Min(_dist, 40);
        Invalidate();
    }

    public void SetView(float yawDeg, float pitchDeg)
    {
        _yaw = yawDeg * MathF.PI / 180;
        _pitch = pitchDeg * MathF.PI / 180;
        Invalidate();
    }

    private Vector3 Eye() =>
        _target + new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _dist;

    private void UpdateMatrices()
    {
        var up = MathF.Cos(_pitch) >= 0 ? Vector3.UnitY : -Vector3.UnitY;
        _view = Matrix4x4.CreateLookAt(Eye(), _target, up);
        float aspect = (float)(Math.Max(1, _panel.ActualWidth) / Math.Max(1, _panel.ActualHeight));
        _proj = Matrix4x4.CreatePerspectiveFieldOfView(35 * MathF.PI / 180, aspect, 0.5f, 5000f);
    }

    private (Vector3 O, Vector3 D) Ray(Windows.Foundation.Point p)
    {
        UpdateMatrices();
        float nx = (float)(2 * p.X / Math.Max(1, _panel.ActualWidth) - 1);
        float ny = (float)(1 - 2 * p.Y / Math.Max(1, _panel.ActualHeight));
        Matrix4x4.Invert(_view * _proj, out var inv);
        var a = Vector4.Transform(new Vector4(nx, ny, 0, 1), inv);
        var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
        var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
        var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (pa, Vector3.Normalize(pb - pa));
    }

    // ───────────── 渲染 ─────────────

    private void RenderNow()
    {
        if (_s == null) return;
        EnsureInit();
        if (!_r.Ready) return;
        UpdateMatrices();
        if (_meshDirty)
        {
            var mesh = MeshBuilder.Build(_s);
            _r.UploadVoxels(mesh.Vertices, mesh.Indices);
            _solid = _s.Model.CountSolid();
            _meshDirty = false;
        }
        if (_linesDirty) { _r.UploadLines(BuildLines()); _linesDirty = false; }
        if (_previewDirty && Tools != null)
        {
            var pm = MeshBuilder.Cubes(Tools.Preview, Tools.PreviewColor);
            _r.UploadPreview(pm.Vertices, pm.Indices);
            _previewDirty = false;
        }
        _r.Render(_view * _proj,
            _s.CheckMode || _s.BeadLook,
            _s.CheckMode ? false : _s.ShowGrid,
            _s.CheckMode ? new Vector3(0.93f, 0.94f, 0.95f) : new Vector3(0.115f, 0.122f, 0.137f),
            _s.CheckMode ? _s.Brightness : 1f,
            _s.CheckMode,
            drawLines: !_s.CheckMode);

        var m = _s.Model;
        _statusText = $"{m.SX} × {m.SY} × {m.SZ}   ·   {_solid:N0} 颗   ·   {m.Palette.Count - 1} 色   ·   {m.PartList.Count} 零件";
        StatusChanged?.Invoke(_statusText, Tools?.HoverInfo);
    }

    private float[] BuildLines()
    {
        var m = _s!.Model;
        var v = new List<float>(4096);
        void L(float x1, float y1, float z1, float x2, float y2, float z2, float r, float g, float b, float a)
        {
            v.Add(x1); v.Add(y1); v.Add(z1); v.Add(r); v.Add(g); v.Add(b); v.Add(a);
            v.Add(x2); v.Add(y2); v.Add(z2); v.Add(r); v.Add(g); v.Add(b); v.Add(a);
        }
        for (int x = 0; x <= m.SX; x++) L(x, 0, 0, x, 0, m.SZ, 0.8f, 0.82f, 0.86f, x % 5 == 0 ? 0.32f : 0.12f);
        for (int z = 0; z <= m.SZ; z++) L(0, 0, z, m.SX, 0, z, 0.8f, 0.82f, 0.86f, z % 5 == 0 ? 0.32f : 0.12f);
        float X = m.SX, Y = m.SY, Z = m.SZ;
        void Edge(float a, float b, float c, float d, float e, float f) => L(a, b, c, d, e, f, 0.55f, 0.58f, 0.63f, 0.45f);
        Edge(0, Y, 0, X, Y, 0); Edge(0, Y, Z, X, Y, Z); Edge(0, Y, 0, 0, Y, Z); Edge(X, Y, 0, X, Y, Z);
        Edge(0, 0, 0, 0, Y, 0); Edge(X, 0, 0, X, Y, 0); Edge(0, 0, Z, 0, Y, Z); Edge(X, 0, Z, X, Y, Z);
        L(0, 0.01f, 0, Math.Min(X, 8), 0.01f, 0, 1f, 0.35f, 0.3f, 1);
        L(0, 0, 0, 0, Math.Min(Y, 8), 0, 0.4f, 0.9f, 0.4f, 1);
        L(0, 0.01f, 0, 0, 0.01f, Math.Min(Z, 8), 0.35f, 0.55f, 1f, 1);
        if (_s.ClipAxis >= 0)
        {
            foreach (float t in new float[] { _s.ClipMin, _s.ClipMax + 1 })
            {
                void C(float a, float b, float c, float d, float e, float f) => L(a, b, c, d, e, f, 1, 0.55f, 0.25f, 0.85f);
                if (_s.ClipAxis == 1) { C(0, t, 0, X, t, 0); C(0, t, Z, X, t, Z); C(0, t, 0, 0, t, Z); C(X, t, 0, X, t, Z); }
                else if (_s.ClipAxis == 0) { C(t, 0, 0, t, Y, 0); C(t, 0, Z, t, Y, Z); C(t, 0, 0, t, 0, Z); C(t, Y, 0, t, Y, Z); }
                else { C(0, 0, t, X, 0, t); C(0, Y, t, X, Y, t); C(0, 0, t, 0, Y, t); C(X, 0, t, X, Y, t); }
            }
        }
        return v.ToArray();
    }

    // ───────────── 输入 ─────────────

    private bool Exploded => _s != null && _s.Explode > 1.01f;

    private static bool Down(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(_panel);
        _last = pt.Position;
        CapturePointer(e.Pointer);
        var props = pt.Properties;
        if (props.IsRightButtonPressed) { _rotating = true; return; }
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceDown)) { _panning = true; return; }
        if (!props.IsLeftButtonPressed || Tools == null || _s == null) return;
        if (Exploded) { Message?.Invoke("爆炸预览中不能编辑，请先把爆炸系数调回 1"); return; }
        bool alt = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (_s.Action == EditAction.Select && !shift && !ctrl && !alt) _s.Selection.Clear();
        var (o, d) = Ray(_last);
        if (Tools.Down(o, d, alt, ctrl)) _meshDirty = true;
        if (_s.Action == EditAction.Select) _meshDirty = true;
        _previewDirty = true;
        Invalidate();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_panel);
        var p = pt.Position;
        double dx = p.X - _last.X, dy = p.Y - _last.Y;
        _last = p;
        if (_rotating)
        {
            _yaw -= (float)dx * 0.008f;
            _pitch = Math.Clamp(_pitch + (float)dy * 0.008f, -1.55f, 1.55f);
            Invalidate();
            return;
        }
        if (_panning)
        {
            UpdateMatrices();
            var right = new Vector3(_view.M11, _view.M21, _view.M31);
            var up = new Vector3(_view.M12, _view.M22, _view.M32);
            _target += (-right * (float)dx + up * (float)dy) * (_dist * 0.0018f);
            Invalidate();
            return;
        }
        if (Tools == null || _s == null || Exploded) return;
        var (o, d) = Ray(p);
        bool alt = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);
        if (pt.Properties.IsLeftButtonPressed && Tools.Drag(o, d)) _meshDirty = true;
        Tools.Hover(o, d, alt);
        _previewDirty = true;
        Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        if (_rotating || _panning) { _rotating = _panning = false; return; }
        if (Tools != null && Tools.Up()) _meshDirty = true;
        _previewDirty = true;
        Invalidate();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(_panel).Properties.MouseWheelDelta;
        _dist = Math.Clamp(_dist * (delta > 0 ? 0.88f : 1.14f), 3, 3000);
        Invalidate();
        e.Handled = true;
    }

    public void SetSpace(bool down) => _spaceDown = down;

    /// <summary>外部（例如工具切换）要求刷新预览。</summary>
    public void RefreshPreview() { Tools?.Preview.Clear(); _previewDirty = true; Invalidate(); }
}
