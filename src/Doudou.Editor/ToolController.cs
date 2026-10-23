using System.Numerics;
using Doudou.Core.Color;
using Doudou.Core.Edit;
using Doudou.Core.Geometry;
using Doudou.Core.Model;
using Br = Doudou.Core.Edit.Brushes;

namespace Doudou.Editor;

/// <summary>
/// 把鼠标操作翻译成编辑动作（仿 MagicaVoxel）：
/// 贴附 / 擦除 / 上色 / 零件刷 / 选择 / 吸色 × 单格 / 整面 / 矩形 / 直线 / 填充 / 平面。
/// </summary>
public sealed class ToolController
{
    private readonly Session _s;
    private bool _stroking;
    private RayHit _start;
    private int _planeAxis;
    private float _planeCoord;
    private readonly HashSet<Int3> _strokeCells = new();
    private EditAction _action;
    private bool _subtract;

    public List<Int3> Preview { get; } = new();
    public Rgb PreviewColor { get; private set; }
    public string? HoverInfo { get; private set; }

    public ToolController(Session s) => _s = s;

    private VoxelModel M => _s.Model;

    public EditAction EffectiveAction(bool alt) => alt ? EditAction.Pick : _s.Action;

    public RayHit? Pick(Vector3 o, Vector3 d) =>
        VoxelRay.Cast(M, o, d, i => _s.IsVisible(i) && !_strokeCells.Contains(M.Pos(i)));

    /// <summary>鼠标悬停：更新预览。</summary>
    public void Hover(Vector3 o, Vector3 d, bool alt)
    {
        Preview.Clear();
        var hit = Pick(o, d);
        HoverInfo = null;
        if (hit is { } h && !h.OnGround)
        {
            int i = M.Index(h.Cell);
            var bead = M.Palette[M.Colors[i]];
            var part = M.Parts[i] != 0 ? M.FindPart(M.Parts[i]) : null;
            HoverInfo = $"{h.Cell}  {bead}  {(part != null ? "零件 " + part.Name : "未分零件")}";
        }
        if (_stroking) { Drag(o, d); return; }
        var act = EffectiveAction(alt);
        if (hit == null || act == EditAction.Pick) return;
        Preview.AddRange(Targets(hit.Value, act, hit.Value));
        PreviewColor = ColorFor(act);
    }

    private Rgb ColorFor(EditAction a) => a switch
    {
        EditAction.Erase => new Rgb(255, 70, 60),
        EditAction.Select => new Rgb(70, 150, 255),
        EditAction.PartPaint => M.FindPart(_s.CurrentPart)?.Tint ?? new Rgb(255, 200, 80),
        _ => _s.CurrentBead.IsAny ? new Rgb(64, 74, 70) : _s.CurrentBead.Rgb,
    };

    /// <summary>按下：开始一笔。返回 true 表示模型被修改。</summary>
    public bool Down(Vector3 o, Vector3 d, bool alt, bool ctrl)
    {
        var act = EffectiveAction(alt);
        var hit = Pick(o, d);
        if (hit == null) return false;
        var h = hit.Value;
        if (act == EditAction.Pick)
        {
            if (h.OnGround) return false;
            int i = M.Index(h.Cell);
            _s.CurrentColor = M.Colors[i];
            if (M.Parts[i] != 0) _s.CurrentPart = M.Parts[i];
            _s.Notify(nameof(Session.CurrentColor));
            return false;
        }
        _action = act;
        _subtract = ctrl;
        _start = h;
        _stroking = true;
        _strokeCells.Clear();
        int axis = h.Normal.X != 0 ? 0 : h.Normal.Y != 0 ? 1 : 2;
        _planeAxis = axis;
        var anchor = act == EditAction.Attach ? h.AttachCell : h.Cell;
        _planeCoord = anchor[axis] + 0.5f;

        if (act == EditAction.PartPaint && _s.CurrentPart == 0)
        {
            var p = M.NewPart();
            _s.CurrentPart = p.Id;
        }

        if (_s.Brush is BrushMode.Box or BrushMode.Line)
        {
            Preview.Clear();
            Preview.AddRange(Targets(h, act, h));
            return false;
        }
        _s.History.Begin(ActionName(act));
        bool changed = Apply(Targets(h, act, h), act);
        if (_s.Brush is not BrushMode.Voxel) Finish();
        return changed;
    }

    /// <summary>拖动：单格笔刷连续涂；矩形/直线更新预览。</summary>
    public bool Drag(Vector3 o, Vector3 d)
    {
        if (!_stroking) return false;
        if (_s.Brush is BrushMode.Box or BrushMode.Line)
        {
            var p = VoxelRay.PlaneHit(o, d, _planeAxis, _planeCoord);
            if (p == null) return false;
            var cur = new Int3((int)MathF.Floor(p.Value.X), (int)MathF.Floor(p.Value.Y), (int)MathF.Floor(p.Value.Z)).With(_planeAxis, (int)MathF.Floor(_planeCoord));
            var startCell = _action == EditAction.Attach ? _start.AttachCell : _start.Cell;
            var cells = _s.Brush == BrushMode.Box ? Br.Box(startCell, cur).ToList() : Br.Line(startCell, cur);
            Preview.Clear();
            Preview.AddRange(Expand(cells, _action));
            PreviewColor = ColorFor(_action);
            return false;
        }
        if (_s.Brush != BrushMode.Voxel) return false;
        var hit = Pick(o, d);
        if (hit == null) return false;
        return Apply(Targets(hit.Value, _action, _start), _action);
    }

    public bool Up()
    {
        if (!_stroking) return false;
        bool changed = false;
        if (_s.Brush is BrushMode.Box or BrushMode.Line)
        {
            _s.History.Begin(ActionName(_action));
            changed = Apply(Preview.ToList(), _action);
        }
        Finish();
        Preview.Clear();
        return changed;
    }

    private void Finish()
    {
        _stroking = false;
        _strokeCells.Clear();
        if (_s.History.InTransaction) _s.History.Commit();
    }

    public void Cancel()
    {
        if (_s.History.InTransaction) _s.History.Cancel();
        _stroking = false;
        _strokeCells.Clear();
        Preview.Clear();
    }

    /// <summary>指针离开视口：清掉悬停信息，状态栏不再显示上一格的豆。</summary>
    public void ClearHover() => HoverInfo = null;

    private static string ActionName(EditAction a) => a switch
    {
        EditAction.Attach => "添加豆子", EditAction.Erase => "删除豆子", EditAction.Paint => "上色",
        EditAction.PartPaint => "分配零件", EditAction.Select => "选择", _ => "编辑",
    };

    /// <summary>根据笔刷形状算出本次作用的格子。</summary>
    private IEnumerable<Int3> Targets(RayHit h, EditAction act, RayHit start)
    {
        if (h.OnGround && act != EditAction.Attach) return Array.Empty<Int3>();
        var normal = h.Normal;
        IEnumerable<Int3> cells;
        switch (_s.Brush)
        {
            case BrushMode.Face:
                cells = h.OnGround ? new[] { h.Cell } : Br.Face(M, h.Cell, normal);
                if (act == EditAction.Attach && !h.OnGround) cells = cells.Select(c => c + normal).ToList();
                break;
            case BrushMode.Fill:
                if (h.OnGround) return Array.Empty<Int3>();
                cells = act == EditAction.Attach ? Br.Face(M, h.Cell, normal).Select(c => c + normal).ToList() : Br.Fill(M, h.Cell);
                break;
            case BrushMode.Plane:
            {
                if (h.OnGround) return Array.Empty<Int3>();
                int axis = _s.PlaneAxis >= 0 ? _s.PlaneAxis : (normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2);
                cells = Br.Plane(M, h.Cell, axis, _s.PlaneSamePart, allow: i => _s.IsVisible(i));
                if (act == EditAction.Attach) cells = cells.Select(c => c + normal).ToList();
                break;
            }
            default:
            {
                var c = act == EditAction.Attach ? h.AttachCell : h.Cell;
                cells = _s.BrushSize == 0 ? new[] { c } : Br.Ball(c, _s.BrushSize, _s.SphereBrush).ToList();
                break;
            }
        }
        return Expand(cells, act);
    }

    private List<Int3> Expand(IEnumerable<Int3> cells, EditAction act)
    {
        var all = Br.Mirrored(M, cells, _s.MirrorX, _s.MirrorY, _s.MirrorZ);
        var res = new List<Int3>();
        foreach (var c in all)
        {
            if (!M.InBounds(c)) continue;
            bool solid = M.Solid(c);
            if (act == EditAction.Attach ? solid : !solid) continue;
            if (act != EditAction.Attach && !_s.IsVisible(M.Index(c))) continue;
            res.Add(c);
        }
        return res;
    }

    private bool Apply(IEnumerable<Int3> cells, EditAction act)
    {
        bool any = false;
        var h = _s.History;
        if (!h.InTransaction) h.Begin(ActionName(act));
        foreach (var c in cells)
        {
            int i = M.Index(c);
            switch (act)
            {
                case EditAction.Attach:
                    if (M.Colors[i] != 0) continue;
                    h.Set(i, _s.CurrentColor == 0 ? (ushort)1 : _s.CurrentColor, 0);
                    _strokeCells.Add(c);
                    any = true;
                    break;
                case EditAction.Erase:
                    if (M.Colors[i] == 0) continue;
                    h.Set(i, 0, 0);
                    any = true;
                    break;
                case EditAction.Paint:
                    if (M.Colors[i] == 0 || M.Colors[i] == _s.CurrentColor) continue;
                    h.SetColor(i, _s.CurrentColor);
                    any = true;
                    break;
                case EditAction.PartPaint:
                    if (M.Colors[i] == 0) continue;
                    var part = M.FindPart(M.Parts[i]);
                    if (part is { Locked: true }) continue;
                    h.SetPart(i, (ushort)_s.CurrentPart);
                    any = true;
                    break;
                case EditAction.Select:
                    if (_subtract) _s.Selection.Remove(i); else _s.Selection.Add(i);
                    any = true;
                    break;
            }
        }
        if (act == EditAction.Select && any) _s.RaiseChanged();
        return any;
    }
}
