namespace Doudou.Core.Geometry;

/// <summary>整数三维坐标。约定：X 向右，Y 向上，Z 指向正面观察者。</summary>
public readonly record struct Int3(int X, int Y, int Z)
{
    public static readonly Int3 Zero = new(0, 0, 0);

    public static Int3 operator +(Int3 a, Int3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Int3 operator -(Int3 a, Int3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Int3 operator *(Int3 a, int k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Int3 operator -(Int3 a) => new(-a.X, -a.Y, -a.Z);

    public int this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

    public Int3 With(int axis, int v) => axis switch
    {
        0 => this with { X = v },
        1 => this with { Y = v },
        _ => this with { Z = v },
    };

    public int ManhattanLength => Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z);

    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>六个带符号轴方向。</summary>
public static class Dir6
{
    public static readonly Int3[] All =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static readonly string[] Names = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };

    /// <summary>方向编号 → 轴（0=X,1=Y,2=Z）。</summary>
    public static int AxisOf(int dir) => dir >> 1;

    /// <summary>方向编号 → 符号（+1 / -1）。</summary>
    public static int SignOf(int dir) => (dir & 1) == 0 ? 1 : -1;

    public static int FromAxisSign(int axis, int sign) => axis * 2 + (sign > 0 ? 0 : 1);

    public static string AxisName(int axis) => axis switch { 0 => "X", 1 => "Y", 2 => "Z", _ => "-" };

    /// <summary>中文方位（用于图纸文案）。</summary>
    public static string ChineseName(int dir) => dir switch
    {
        0 => "向右", 1 => "向左", 2 => "向上", 3 => "向下", 4 => "向前", _ => "向后",
    };
}
