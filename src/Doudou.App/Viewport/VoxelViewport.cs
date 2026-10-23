using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Doudou.Core.Color;
using Doudou.Core.Geometry;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using NVec3 = System.Numerics.Vector3;

using Doudou.Editor;
namespace Doudou.App.Viewport;

/// <summary>
/// 三维编辑视口（OpenGL）。操作仿 MagicaVoxel：
///   左键 = 当前工具；右键拖 = 旋转；中键拖 / 空格+左键 = 平移；滚轮 = 缩放；Alt+左键 = 吸色；F = 居中。
/// </summary>
public sealed class VoxelViewport : Grid
{
    private readonly GLWpfControl _gl = new();
    private readonly TextBlock _overlay = new()
    {
        Margin = new Thickness(10, 8, 0, 0),
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Left,
        IsHitTestVisible = false,
        FontSize = 12,
    };
    private readonly TextBlock _help = new()
    {
        Margin = new Thickness(0, 0, 10, 8),
        VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Right,
        IsHitTestVisible = false,
        FontSize = 11.5,
        Opacity = 0.6,
        Text = "右键拖动 旋转 · 中键拖动 平移 · 滚轮 缩放 · Alt+左键 吸色 · F 居中 · Ctrl+Z/Y 撤销/重做",
    };

    private Session? _s;
    public ToolController? Tools { get; private set; }

    private Shader? _voxShader, _lineShader;
    private GpuMesh? _voxMesh, _previewMesh, _lineMesh;
    private bool _meshDirty = true, _previewDirty = true, _linesDirty = true;
    private MeshBuilder.Mesh? _mesh;
    private bool _glReady;

    // 相机
    private Vector3 _target;
    private float _yaw = 0.6f, _pitch = 0.45f, _dist = 80;
    private Matrix4 _view, _proj;

    private Point _lastMouse;
    private bool _rotating, _panning, _spaceDown;

    public VoxelViewport()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        Children.Add(_gl);
        Children.Add(_overlay);
        Children.Add(_help);
        _overlay.Foreground = System.Windows.Media.Brushes.Gainsboro;
        _help.Foreground = System.Windows.Media.Brushes.Gainsboro;
        _gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        _gl.Render += OnRender;
        _gl.MouseDown += OnMouseDown;
        _gl.MouseUp += OnMouseUp;
        _gl.MouseMove += OnMouseMove;
        _gl.MouseWheel += OnWheel;
        _gl.MouseLeave += (_, _) => { Tools?.Preview.Clear(); _previewDirty = true; Redraw(); };
        Focusable = true;
    }

    public void Attach(Session s)
    {
        _s = s;
        Tools = new ToolController(s);
        s.ModelChanged += () => { _meshDirty = true; _linesDirty = true; Redraw(); };
        s.ModelReplaced += () => { FrameModel(); _meshDirty = true; _linesDirty = true; Redraw(); };
        FrameModel();
    }

    public void Redraw() => _gl.InvalidateVisual();

    public void FrameModel()
    {
        if (_s == null) return;
        var m = _s.Model;
        var b = m.Bounds();
        if (b is { } bb)
        {
            _target = new Vector3((bb.Min.X + bb.Max.X + 1) / 2f, (bb.Min.Y + bb.Max.Y + 1) / 2f, (bb.Min.Z + bb.Max.Z + 1) / 2f);
            var ext = bb.Max - bb.Min;
            _dist = Math.Max(20, Math.Max(ext.X, Math.Max(ext.Y, ext.Z)) * 2.4f);
        }
        else
        {
            _target = new Vector3(m.SX / 2f, m.SY / 4f, m.SZ / 2f);
            _dist = Math.Max(m.SX, m.SZ) * 2.2f;
        }
        Redraw();
    }

    /// <summary>把相机对准某一格（漏色检查定位用）。</summary>
    public void FocusCell(Int3 c)
    {
        _target = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
        _dist = Math.Min(_dist, 40);
        Redraw();
    }

    public void SetView(float yawDeg, float pitchDeg)
    {
        _yaw = MathHelper.DegreesToRadians(yawDeg);
        _pitch = MathHelper.DegreesToRadians(pitchDeg);
        Redraw();
    }

    // ───────────── 渲染 ─────────────

    private void InitGl()
    {
        _voxShader = new Shader(Shaders.VoxelVS, Shaders.VoxelFS);
        _lineShader = new Shader(Shaders.LineVS, Shaders.LineFS);
        _voxMesh = new GpuMesh(3, 3, 3, 2, 1);
        _previewMesh = new GpuMesh(3, 3, 3, 2, 1);
        _lineMesh = new GpuMesh(3, 4);
        _glReady = true;
    }

    private Vector3 Eye()
    {
        var dir = new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        return _target + dir * _dist;
    }

    private void UpdateMatrices()
    {
        double w = Math.Max(1, _gl.ActualWidth), h = Math.Max(1, _gl.ActualHeight);
        var up = MathF.Cos(_pitch) >= 0 ? Vector3.UnitY : -Vector3.UnitY;
        _view = Matrix4.LookAt(Eye(), _target, up);
        _proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(35), (float)(w / h), 0.5f, 5000f);
    }

    private void OnRender(TimeSpan _)
    {
        if (_s == null) return;
        if (!_glReady) InitGl();
        UpdateMatrices();
        if (_meshDirty)
        {
            _mesh = MeshBuilder.Build(_s);
            _voxMesh!.Upload(_mesh.Vertices, _mesh.Indices);
            _meshDirty = false;
        }
        if (_linesDirty) { BuildLines(); _linesDirty = false; }
        if (_previewDirty && Tools != null)
        {
            var pm = MeshBuilder.Cubes(Tools.Preview, Tools.PreviewColor);
            _previewMesh!.Upload(pm.Vertices, pm.Indices);
            _previewDirty = false;
        }

        GL.Viewport(0, 0, Math.Max(1, _gl.FrameBufferWidth), Math.Max(1, _gl.FrameBufferHeight));
        GL.ClearColor(0.165f, 0.176f, 0.2f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        var vp = _view * _proj;

        _lineShader!.Use();
        _lineShader.Set("uVP", vp);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _lineMesh!.DrawArrays(PrimitiveType.Lines);

        _voxShader!.Use();
        _voxShader.Set("uVP", vp);
        _voxShader.Set("uBead", _s.BeadLook ? 1 : 0);
        _voxShader.Set("uGrid", _s.ShowGrid ? 1 : 0);
        _voxShader.Set("uAlpha", 1f);
        GL.Disable(EnableCap.Blend);
        _voxMesh!.DrawElements();

        if (Tools != null && Tools.Preview.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.DepthMask(false);
            _voxShader.Set("uAlpha", 0.55f);
            _voxShader.Set("uBead", 0);
            _previewMesh!.DrawElements();
            GL.DepthMask(true);
        }

        var m = _s.Model;
        int solid = 0;
        foreach (var c in m.Colors) if (c != 0) solid++;
        _overlay.Text = $"{m.SX}×{m.SY}×{m.SZ}   {solid} 颗   {m.Palette.Count - 1} 色   {m.PartList.Count} 零件" +
                        (Tools?.HoverInfo != null ? "\n" + Tools.HoverInfo : "");
    }

    private void BuildLines()
    {
        var m = _s!.Model;
        var v = new List<float>();
        void L(float x1, float y1, float z1, float x2, float y2, float z2, float r, float g, float b, float a)
        {
            v.AddRange(new[] { x1, y1, z1, r, g, b, a, x2, y2, z2, r, g, b, a });
        }
        // 地面网格（每 5 格加深）
        for (int x = 0; x <= m.SX; x++)
        {
            float a = x % 5 == 0 ? 0.35f : 0.14f;
            L(x, 0, 0, x, 0, m.SZ, 0.8f, 0.82f, 0.86f, a);
        }
        for (int z = 0; z <= m.SZ; z++)
        {
            float a = z % 5 == 0 ? 0.35f : 0.14f;
            L(0, 0, z, m.SX, 0, z, 0.8f, 0.82f, 0.86f, a);
        }
        // 画布边框
        float X = m.SX, Y = m.SY, Z = m.SZ;
        var c = (0.55f, 0.58f, 0.63f, 0.5f);
        L(0, Y, 0, X, Y, 0, c.Item1, c.Item2, c.Item3, c.Item4); L(0, Y, Z, X, Y, Z, c.Item1, c.Item2, c.Item3, c.Item4);
        L(0, Y, 0, 0, Y, Z, c.Item1, c.Item2, c.Item3, c.Item4); L(X, Y, 0, X, Y, Z, c.Item1, c.Item2, c.Item3, c.Item4);
        L(0, 0, 0, 0, Y, 0, c.Item1, c.Item2, c.Item3, c.Item4); L(X, 0, 0, X, Y, 0, c.Item1, c.Item2, c.Item3, c.Item4);
        L(0, 0, Z, 0, Y, Z, c.Item1, c.Item2, c.Item3, c.Item4); L(X, 0, Z, X, Y, Z, c.Item1, c.Item2, c.Item3, c.Item4);
        // 坐标轴
        L(0, 0.01f, 0, Math.Min(X, 8), 0.01f, 0, 1f, 0.35f, 0.3f, 1);
        L(0, 0, 0, 0, Math.Min(Y, 8), 0, 0.4f, 0.9f, 0.4f, 1);
        L(0, 0.01f, 0, 0, 0.01f, Math.Min(Z, 8), 0.35f, 0.55f, 1f, 1);
        // 剖切层指示
        if (_s.ClipAxis >= 0)
        {
            float lo = _s.ClipMin, hi = _s.ClipMax + 1;
            foreach (var t in new[] { lo, hi })
            {
                if (_s.ClipAxis == 1) { L(0, t, 0, X, t, 0, 1, 0.55f, 0.25f, 0.8f); L(0, t, Z, X, t, Z, 1, 0.55f, 0.25f, 0.8f); L(0, t, 0, 0, t, Z, 1, 0.55f, 0.25f, 0.8f); L(X, t, 0, X, t, Z, 1, 0.55f, 0.25f, 0.8f); }
                else if (_s.ClipAxis == 0) { L(t, 0, 0, t, Y, 0, 1, 0.55f, 0.25f, 0.8f); L(t, 0, Z, t, Y, Z, 1, 0.55f, 0.25f, 0.8f); L(t, 0, 0, t, 0, Z, 1, 0.55f, 0.25f, 0.8f); L(t, Y, 0, t, Y, Z, 1, 0.55f, 0.25f, 0.8f); }
                else { L(0, 0, t, X, 0, t, 1, 0.55f, 0.25f, 0.8f); L(0, Y, t, X, Y, t, 1, 0.55f, 0.25f, 0.8f); L(0, 0, t, 0, Y, t, 1, 0.55f, 0.25f, 0.8f); L(X, 0, t, X, Y, t, 1, 0.55f, 0.25f, 0.8f); }
            }
        }
        _lineMesh!.Upload(v.ToArray(), null);
    }

    // ───────────── 输入 ─────────────

    private (NVec3 O, NVec3 D) Ray(Point p)
    {
        UpdateMatrices();
        double w = Math.Max(1, _gl.ActualWidth), h = Math.Max(1, _gl.ActualHeight);
        float nx = (float)(2 * p.X / w - 1), ny = (float)(1 - 2 * p.Y / h);
        var inv = Matrix4.Invert(_view * _proj);
        var a = new Vector4(nx, ny, -1, 1) * inv;
        var b = new Vector4(nx, ny, 1, 1) * inv;
        var pa = a.Xyz / a.W; var pb = b.Xyz / b.W;
        var d = (pb - pa).Normalized();
        return (new NVec3(pa.X, pa.Y, pa.Z), new NVec3(d.X, d.Y, d.Z));
    }

    private bool Exploded => _s != null && _s.Explode > 1.01f;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _gl.CaptureMouse();
        _lastMouse = e.GetPosition(_gl);
        if (e.ChangedButton == MouseButton.Right) { _rotating = true; return; }
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown)) { _panning = true; return; }
        if (e.ChangedButton != MouseButton.Left || Tools == null || _s == null) return;
        if (Exploded) { _s.Say("爆炸预览中不能编辑，先把爆炸系数调回 1"); return; }
        if (e.ClickCount == 2 && _s.Action == Doudou.Core.Edit.EditAction.Select) { _s.Selection.Clear(); _s.RaiseChanged(); return; }
        var (o, d) = Ray(_lastMouse);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_s.Action == Doudou.Core.Edit.EditAction.Select && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !ctrl && !alt)
            _s.Selection.Clear();
        if (Tools.Down(o, d, alt, ctrl)) _meshDirty = true;
        _previewDirty = true;
        Redraw();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _gl.ReleaseMouseCapture();
        if (_rotating || _panning) { _rotating = _panning = false; return; }
        if (e.ChangedButton == MouseButton.Left && Tools != null)
        {
            if (Tools.Up()) _meshDirty = true;
            _previewDirty = true;
            Redraw();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_gl);
        var delta = p - _lastMouse;
        _lastMouse = p;
        if (_rotating)
        {
            _yaw -= (float)delta.X * 0.008f;
            _pitch = Math.Clamp(_pitch + (float)delta.Y * 0.008f, -1.55f, 1.55f);
            Redraw();
            return;
        }
        if (_panning)
        {
            UpdateMatrices();
            var right = new Vector3(_view.M11, _view.M21, _view.M31);
            var up = new Vector3(_view.M12, _view.M22, _view.M32);
            float k = _dist * 0.0018f;
            _target += (-right * (float)delta.X + up * (float)delta.Y) * k;
            Redraw();
            return;
        }
        if (Tools == null || _s == null || Exploded) return;
        var (o, d) = Ray(p);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (e.LeftButton == MouseButtonState.Pressed && Tools.Drag(o, d)) _meshDirty = true;
        Tools.Hover(o, d, alt);
        _previewDirty = true;
        Redraw();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.88f : 1.14f), 3, 3000);
        Redraw();
    }

    public void SetSpace(bool down) => _spaceDown = down;
}
