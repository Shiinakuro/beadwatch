using Doudou.Core.Model;

namespace Doudou.Core.Edit;

/// <summary>
/// 撤销 / 重做。普通编辑记录逐格差异；改尺寸、旋转等整体操作记录整模型快照。
/// 用法：Begin("名称") → 多次 Set(...) → Commit()。
/// </summary>
public sealed class EditHistory
{
    private sealed class Record
    {
        public string Name = "";
        public Dictionary<int, (ushort OldC, ushort NewC, ushort OldP, ushort NewP)> Cells = new();
        public VoxelModel? Before, After;
        public List<PartInfo>? PartsBefore, PartsAfter;
        public List<BeadColor>? PaletteBefore, PaletteAfter;
    }

    private readonly VoxelModel _m;
    private readonly List<Record> _undo = new();
    private readonly List<Record> _redo = new();
    private Record? _cur;
    public int Limit { get; set; } = 300;

    public event Action? Changed;

    public EditHistory(VoxelModel m) => _m = m;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;
    public bool InTransaction => _cur != null;

    public void Begin(string name)
    {
        if (_cur != null) Commit();
        _cur = new Record
        {
            Name = name,
            PartsBefore = _m.PartList.Select(p => p.Clone()).ToList(),
            PaletteBefore = _m.Palette.Select(c => c.Clone()).ToList(),
        };
    }

    /// <summary>设置一格颜色与零件号（color=0 表示删除）。</summary>
    public void Set(int index, ushort color, ushort part)
    {
        if (_cur == null) throw new InvalidOperationException("未开始编辑事务");
        if (color == 0) part = 0;
        ushort oc = _m.Colors[index], op = _m.Parts[index];
        if (oc == color && op == part) return;
        if (_cur.Cells.TryGetValue(index, out var e)) _cur.Cells[index] = (e.OldC, color, e.OldP, part);
        else _cur.Cells[index] = (oc, color, op, part);
        _m.Colors[index] = color;
        _m.Parts[index] = part;
    }

    public void SetColor(int index, ushort color) => Set(index, color, color == 0 ? (ushort)0 : _m.Parts[index]);
    public void SetPart(int index, ushort part) { if (_m.Colors[index] != 0) Set(index, _m.Colors[index], part); }

    /// <summary>整体操作：传入操作前的快照，Commit 时自动记录操作后的状态。</summary>
    public void BeginSnapshot(string name)
    {
        if (_cur != null) Commit();
        _cur = new Record { Name = name, Before = _m.Clone() };
    }

    public bool Commit()
    {
        var r = _cur;
        _cur = null;
        if (r == null) return false;
        if (r.Before != null)
        {
            r.After = _m.Clone();
        }
        else
        {
            foreach (var k in r.Cells.Where(kv => kv.Value.OldC == kv.Value.NewC && kv.Value.OldP == kv.Value.NewP).Select(kv => kv.Key).ToList())
                r.Cells.Remove(k);
            bool partsChanged = !SameParts(r.PartsBefore!, _m.PartList);
            bool paletteChanged = r.PaletteBefore!.Count != _m.Palette.Count;
            if (r.Cells.Count == 0 && !partsChanged && !paletteChanged) { _m.Touch(); return false; }
            r.PartsAfter = _m.PartList.Select(p => p.Clone()).ToList();
            r.PaletteAfter = _m.Palette.Select(c => c.Clone()).ToList();
        }
        _undo.Add(r);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        _m.Touch();
        Changed?.Invoke();
        return true;
    }

    public void Cancel()
    {
        var r = _cur;
        _cur = null;
        if (r == null) return;
        if (r.Before != null) _m.CopyFrom(r.Before);
        else
        {
            foreach (var (i, v) in r.Cells) { _m.Colors[i] = v.OldC; _m.Parts[i] = v.OldP; }
            Restore(_m.PartList, r.PartsBefore!);
            Restore(_m.Palette, r.PaletteBefore!);
        }
        _m.Touch();
    }

    public bool Undo()
    {
        if (_cur != null) Commit();
        if (_undo.Count == 0) return false;
        var r = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        if (r.Before != null) _m.CopyFrom(r.Before);
        else
        {
            foreach (var (i, v) in r.Cells) { _m.Colors[i] = v.OldC; _m.Parts[i] = v.OldP; }
            Restore(_m.PartList, r.PartsBefore!);
            Restore(_m.Palette, r.PaletteBefore!);
        }
        _redo.Add(r);
        _m.Touch();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var r = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        if (r.After != null) _m.CopyFrom(r.After);
        else
        {
            Restore(_m.Palette, r.PaletteAfter!);
            foreach (var (i, v) in r.Cells) { _m.Colors[i] = v.NewC; _m.Parts[i] = v.NewP; }
            Restore(_m.PartList, r.PartsAfter!);
        }
        _undo.Add(r);
        _m.Touch();
        Changed?.Invoke();
        return true;
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); _cur = null; Changed?.Invoke(); }

    private static void Restore<T>(List<T> target, List<T> src) where T : class
    {
        target.Clear();
        foreach (var x in src) target.Add(x is PartInfo p ? (T)(object)p.Clone() : x is BeadColor c ? (T)(object)c.Clone() : x);
    }

    private static bool SameParts(List<PartInfo> a, List<PartInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Id != y.Id || x.Name != y.Name || x.PlaneAxis != y.PlaneAxis || x.Visible != y.Visible || x.Flip != y.Flip
                || x.Group != y.Group || x.Note != y.Note || x.Locked != y.Locked) return false;
        }
        return true;
    }
}
