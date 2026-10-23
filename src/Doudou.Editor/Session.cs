using System.ComponentModel;
using System.Runtime.CompilerServices;
using Doudou.Core.Color;
using Doudou.Core.Edit;
using Doudou.Core.IO;
using Doudou.Core.Model;
using Doudou.Core.Recon;
using Doudou.Core.Services;

namespace Doudou.Editor;

public enum DisplayMode { Color, PartTint }

/// <summary>整个程序共享的编辑状态（当前模型、工具、显示选项）。</summary>
public sealed class Session : INotifyPropertyChanged
{
    public PaletteLibrary Lib { get; }
    public AppSettings Settings { get; }
    public Hy3dClient Hy3d { get; } = new();

    public VoxelModel Model { get; private set; }
    public EditHistory History { get; private set; }
    public ProjectMeta Meta { get; private set; } = new();
    /// <summary>参考视图图片（重建用 / 漏色比对用）。</summary>
    public Dictionary<ViewKind, string> ViewFiles { get; private set; } = new();
    private List<ViewImage>? _refViews;

    public string? ProjectPath { get; set; }
    private bool _dirty;
    public bool Dirty { get => _dirty; set => Set(ref _dirty, value); }

    /// <summary>模型被整体替换（新建/打开/重建）。</summary>
    public event Action? ModelReplaced;
    /// <summary>模型内容或显示选项变化，视口需要重建网格。</summary>
    public event Action? ModelChanged;
    /// <summary>只影响渲染参数（外观开关、亮度）的变化：重画一帧即可，不必重建网格。</summary>
    public event Action? RenderChanged;
    /// <summary>模型内容变化（编辑事务提交 / 撤销 / 重做 / 整体替换），与显示选项无关。</summary>
    public event Action? ContentChanged;
    public event Action<string>? Status;

    public Session()
    {
        Lib = PaletteLibrary.LoadDirectory(AppPaths.Palettes);
        Settings = AppSettings.Load();
        Model = NewDefaultModel();
        History = new EditHistory(Model);
        History.Changed += OnHistoryChanged;
        _currentColor = 1;
    }

    private VoxelModel NewDefaultModel(int sx = 32, int sy = 32, int sz = 32)
    {
        var m = new VoxelModel(sx, sy, sz) { BrandId = Settings.DefaultBrand, Name = "未命名" };
        var brand = Lib.Brands.GetValueOrDefault(m.BrandId) ?? Lib.Brands.Values.First();
        m.BrandId = brand.Id;
        var white = brand.Colors.FirstOrDefault(c => c.Code == "H2") ?? brand.Colors[0];
        m.EnsureColor(white.Code, white.Rgb);
        return m;
    }

    private void OnHistoryChanged()
    {
        Dirty = true;
        ContentChanged?.Invoke();
        RaiseChanged();
    }

    public void RaiseChanged() => ModelChanged?.Invoke();
    public void Say(string s) => Status?.Invoke(s);

    public void Replace(VoxelModel m, ProjectMeta? meta = null, Dictionary<ViewKind, string>? views = null, string? path = null)
    {
        History.Changed -= OnHistoryChanged;
        Model = m;
        History = new EditHistory(m);
        History.Changed += OnHistoryChanged;
        if (meta != null) Meta = meta;
        if (views != null) { ViewFiles = views; _refViews = null; }
        ProjectPath = path;
        Selection.Clear();
        Highlight.Clear();
        _currentColor = (ushort)Math.Min(1, m.Palette.Count - 1);
        CurrentPart = m.PartList.FirstOrDefault()?.Id ?? 0;
        ClipAxis = -1;
        Dirty = path == null;
        ModelReplaced?.Invoke();
        ContentChanged?.Invoke();
        RaiseChanged();
        OnPropertyChanged(nameof(Model));
    }

    public void NewBlank(int sx, int sy, int sz) => Replace(NewDefaultModel(sx, sy, sz), new ProjectMeta(), new Dictionary<ViewKind, string>());

    /// <summary>参考视图（懒加载，供漏色比对）。</summary>
    public List<ViewImage> RefViews()
    {
        if (_refViews != null) return _refViews;
        _refViews = new List<ViewImage>();
        foreach (var (k, f) in ViewFiles)
        {
            try { if (System.IO.File.Exists(f)) _refViews.Add(ViewImage.Load(k, f)); } catch (Exception) { }
        }
        return _refViews;
    }

    // ───────── 工具状态 ─────────
    private EditAction _action = EditAction.Attach;
    public EditAction Action { get => _action; set => Set(ref _action, value); }

    private BrushMode _brush = BrushMode.Voxel;
    public BrushMode Brush { get => _brush; set => Set(ref _brush, value); }

    private int _brushSize;
    /// <summary>单格笔刷半径（0 = 1 颗）。</summary>
    public int BrushSize { get => _brushSize; set => Set(ref _brushSize, Math.Clamp(value, 0, 12)); }

    private bool _sphere = true;
    public bool SphereBrush { get => _sphere; set => Set(ref _sphere, value); }

    private bool _mx, _my, _mz;
    public bool MirrorX { get => _mx; set => Set(ref _mx, value); }
    public bool MirrorY { get => _my; set => Set(ref _my, value); }
    public bool MirrorZ { get => _mz; set => Set(ref _mz, value); }

    private ushort _currentColor;
    public ushort CurrentColor
    {
        get => _currentColor;
        set { if (value < Model.Palette.Count) Set(ref _currentColor, value); }
    }

    private int _currentPart;
    public int CurrentPart { get => _currentPart; set => Set(ref _currentPart, value); }

    /// <summary>平面填充（零件刷）沿哪个轴：-1 = 跟随点击面的法线。</summary>
    private int _planeAxis = -1;
    public int PlaneAxis { get => _planeAxis; set => Set(ref _planeAxis, value); }

    private bool _planeSamePart = true;
    /// <summary>平面填充只扩散到与起点同零件号的豆。</summary>
    public bool PlaneSamePart { get => _planeSamePart; set => Set(ref _planeSamePart, value); }

    // ───────── 显示 ─────────
    private DisplayMode _display = DisplayMode.Color;
    public DisplayMode Display { get => _display; set { if (Set(ref _display, value)) RaiseChanged(); } }

    private bool _beadLook = true;
    public bool BeadLook { get => _beadLook; set { if (Set(ref _beadLook, value)) RenderChanged?.Invoke(); } }

    private bool _showGrid = true;
    public bool ShowGrid { get => _showGrid; set { if (Set(ref _showGrid, value)) RenderChanged?.Invoke(); } }

    private bool _checkMode;
    /// <summary>成品检查模式：圆柱豆外观 + 柔光 + 亮背景、隐藏网格线，相机不动，一眼看成品效果（C）。</summary>
    public bool CheckMode { get => _checkMode; set { if (Set(ref _checkMode, value)) RenderChanged?.Invoke(); } }

    private float _brightness = 1f;
    /// <summary>成品检查模式下的曝光倍率。</summary>
    public float Brightness { get => _brightness; set { if (Set(ref _brightness, Math.Clamp(value, 0.5f, 1.6f))) RenderChanged?.Invoke(); } }

    private bool _hideSelection;
    /// <summary>临时隐藏选中的豆：空心化后把外壳选起来藏掉，检查内腔（H）。</summary>
    public bool HideSelection { get => _hideSelection; set { if (Set(ref _hideSelection, value)) RaiseChanged(); } }

    private float _explode = 1f;
    /// <summary>爆炸系数：1 = 组装状态。</summary>
    public float Explode { get => _explode; set { if (Set(ref _explode, value)) RaiseChanged(); } }

    private int _clipAxis = -1;
    public int ClipAxis { get => _clipAxis; set { if (Set(ref _clipAxis, value)) RaiseChanged(); } }
    private int _clipMin, _clipMax = 9999;
    public int ClipMin { get => _clipMin; set { if (Set(ref _clipMin, value)) RaiseChanged(); } }
    public int ClipMax { get => _clipMax; set { if (Set(ref _clipMax, value)) RaiseChanged(); } }

    public HashSet<int> Selection { get; } = new();
    public HashSet<int> Highlight { get; } = new();

    /// <summary>某格当前是否显示（零件隐藏 / 剖切）。</summary>
    public bool IsVisible(int i)
    {
        if (Model.Colors[i] == 0) return false;
        if (_hideSelection && Selection.Contains(i)) return false;
        int pid = Model.Parts[i];
        if (pid != 0)
        {
            var p = Model.FindPart(pid);
            if (p != null && !p.Visible) return false;
        }
        if (ClipAxis >= 0)
        {
            int c = Model.Pos(i)[ClipAxis];
            if (c < ClipMin || c > ClipMax) return false;
        }
        return true;
    }

    public BeadColor CurrentBead => Model.Palette[Math.Min(CurrentColor, (ushort)(Model.Palette.Count - 1))];

    /// <summary>从品牌色卡选色（加入模型调色板并设为当前色）。</summary>
    public void UseBrandColor(BrandColor c)
    {
        CurrentColor = Model.EnsureColor(c.Code, c.Rgb);
        OnPropertyChanged(nameof(CurrentColor));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
    public void Notify(string name) => OnPropertyChanged(name);
}
