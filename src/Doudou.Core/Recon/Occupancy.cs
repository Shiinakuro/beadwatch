using Doudou.Core.Geometry;

namespace Doudou.Core.Recon;

/// <summary>只有占位、没有颜色的体素网格（重建中间结果）。</summary>
public sealed class Occupancy
{
    public int SX { get; }
    public int SY { get; }
    public int SZ { get; }
    public bool[] Cells { get; }

    public Occupancy(int sx, int sy, int sz)
    {
        SX = sx; SY = sy; SZ = sz;
        Cells = new bool[sx * sy * sz];
    }

    public int Index(int x, int y, int z) => (z * SY + y) * SX + x;
    public bool In(int x, int y, int z) => (uint)x < (uint)SX && (uint)y < (uint)SY && (uint)z < (uint)SZ;
    public bool this[int x, int y, int z]
    {
        get => In(x, y, z) && Cells[Index(x, y, z)];
        set { if (In(x, y, z)) Cells[Index(x, y, z)] = value; }
    }

    public int Count() => Cells.Count(c => c);

    public (Int3 Min, Int3 Max)? Bounds()
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        for (int z = 0; z < SZ; z++)
            for (int y = 0; y < SY; y++)
                for (int x = 0; x < SX; x++)
                    if (Cells[Index(x, y, z)])
                    {
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                        y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                        z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
                    }
        return x1 < 0 ? null : (new Int3(x0, y0, z0), new Int3(x1, y1, z1));
    }

    public Occupancy Cropped()
    {
        var b = Bounds();
        if (b == null) return this;
        var (mn, mx) = b.Value;
        var o = new Occupancy(mx.X - mn.X + 1, mx.Y - mn.Y + 1, mx.Z - mn.Z + 1);
        for (int z = 0; z < o.SZ; z++)
            for (int y = 0; y < o.SY; y++)
                for (int x = 0; x < o.SX; x++)
                    o.Cells[o.Index(x, y, z)] = Cells[Index(x + mn.X, y + mn.Y, z + mn.Z)];
        return o;
    }

    /// <summary>绕 Y 轴旋转 90°×k，可选先沿 X 镜像。</summary>
    public Occupancy Transformed(int k, bool mirrorX)
    {
        k = ((k % 4) + 4) % 4;
        int nx = k % 2 == 0 ? SX : SZ, nz = k % 2 == 0 ? SZ : SX;
        var o = new Occupancy(nx, SY, nz);
        for (int z = 0; z < SZ; z++)
            for (int y = 0; y < SY; y++)
                for (int x = 0; x < SX; x++)
                {
                    if (!Cells[Index(x, y, z)]) continue;
                    int px = mirrorX ? SX - 1 - x : x;
                    var (qx, qz) = k switch
                    {
                        0 => (px, z),
                        1 => (z, SX - 1 - px),
                        2 => (SX - 1 - px, SZ - 1 - z),
                        _ => (SZ - 1 - z, px),
                    };
                    o.Cells[o.Index(qx, y, qz)] = true;
                }
        return o;
    }

    /// <summary>只保留最大的 6 邻域连通块（去掉漂浮碎片）。</summary>
    public void KeepLargestComponent(double keepRatio = 0.05)
    {
        var label = new int[Cells.Length];
        var sizes = new List<int> { 0 };
        var q = new Queue<int>();
        for (int i = 0; i < Cells.Length; i++)
        {
            if (!Cells[i] || label[i] != 0) continue;
            int id = sizes.Count, n = 0;
            label[i] = id; q.Enqueue(i);
            while (q.Count > 0)
            {
                int j = q.Dequeue(); n++;
                int x = j % SX, t = j / SX, y = t % SY, z = t / SY;
                foreach (var d in Dir6.All)
                {
                    int ax = x + d.X, ay = y + d.Y, az = z + d.Z;
                    if (!In(ax, ay, az)) continue;
                    int k = Index(ax, ay, az);
                    if (Cells[k] && label[k] == 0) { label[k] = id; q.Enqueue(k); }
                }
            }
            sizes.Add(n);
        }
        if (sizes.Count <= 2) return;
        int largest = sizes.Max();
        for (int i = 0; i < Cells.Length; i++)
            if (Cells[i] && sizes[label[i]] < largest * keepRatio) Cells[i] = false;
    }

    /// <summary>填掉与外界不连通的内部空腔。</summary>
    public void FillCavities()
    {
        var outside = new bool[Cells.Length];
        var q = new Queue<int>();
        void Seed(int x, int y, int z)
        {
            int i = Index(x, y, z);
            if (!Cells[i] && !outside[i]) { outside[i] = true; q.Enqueue(i); }
        }
        for (int z = 0; z < SZ; z++)
            for (int y = 0; y < SY; y++) { Seed(0, y, z); Seed(SX - 1, y, z); }
        for (int z = 0; z < SZ; z++)
            for (int x = 0; x < SX; x++) { Seed(x, 0, z); Seed(x, SY - 1, z); }
        for (int y = 0; y < SY; y++)
            for (int x = 0; x < SX; x++) { Seed(x, y, 0); Seed(x, y, SZ - 1); }
        while (q.Count > 0)
        {
            int j = q.Dequeue();
            int x = j % SX, t = j / SX, y = t % SY, z = t / SY;
            foreach (var d in Dir6.All)
            {
                int ax = x + d.X, ay = y + d.Y, az = z + d.Z;
                if (!In(ax, ay, az)) continue;
                int k = Index(ax, ay, az);
                if (!Cells[k] && !outside[k]) { outside[k] = true; q.Enqueue(k); }
            }
        }
        for (int i = 0; i < Cells.Length; i++) if (!outside[i]) Cells[i] = true;
    }
}
