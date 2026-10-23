using System.Numerics;
using Doudou.Core.Imaging;

namespace Doudou.Core.Recon;

public enum ViewKind { Front, Back, Left, Right, Top, Bottom }

/// <summary>
/// 正交视图的相机约定（模型坐标：X 右、Y 上、Z 指向正面）。
///   Dir   = 从物体指向相机的方向
///   Right = 图像向右对应的模型方向
///   Up    = 图像向上对应的模型方向
/// 左视图 = 站在物体左侧看（物体面朝画面左侧）；物体自身的左侧在 +X。
/// </summary>
public static class ViewGeometry
{
    public static Vector3 Dir(ViewKind v) => v switch
    {
        ViewKind.Front => new(0, 0, 1),
        ViewKind.Back => new(0, 0, -1),
        ViewKind.Left => new(1, 0, 0),
        ViewKind.Right => new(-1, 0, 0),
        ViewKind.Top => new(0, 1, 0),
        _ => new(0, -1, 0),
    };

    public static Vector3 Right(ViewKind v) => v switch
    {
        ViewKind.Front => new(1, 0, 0),
        ViewKind.Back => new(-1, 0, 0),
        ViewKind.Left => new(0, 0, -1),
        ViewKind.Right => new(0, 0, 1),
        ViewKind.Top => new(1, 0, 0),
        _ => new(1, 0, 0),
    };

    public static Vector3 Up(ViewKind v) => v switch
    {
        ViewKind.Top => new(0, 0, -1),
        ViewKind.Bottom => new(0, 0, 1),
        _ => new(0, 1, 0),
    };

    public static string Chinese(ViewKind v) => v switch
    {
        ViewKind.Front => "正面", ViewKind.Back => "背面", ViewKind.Left => "左侧",
        ViewKind.Right => "右侧", ViewKind.Top => "顶部", _ => "底部",
    };

    /// <summary>混元 2mv 的视图键名（只支持四个水平视图）。</summary>
    public static string? HunyuanKey(ViewKind v) => v switch
    {
        ViewKind.Front => "front", ViewKind.Back => "back", ViewKind.Left => "left", ViewKind.Right => "right", _ => null,
    };

    /// <summary>主方向轴与符号：例如 Front → (轴 2, +1)。</summary>
    public static (int Axis, int Sign) AxisSign(ViewKind v)
    {
        var d = Dir(v);
        if (d.X != 0) return (0, Math.Sign(d.X));
        if (d.Y != 0) return (1, Math.Sign(d.Y));
        return (2, Math.Sign(d.Z));
    }
}

/// <summary>一张参考视图：原图 + 前景轮廓。</summary>
public sealed class ViewImage
{
    public ViewKind Kind { get; init; }
    public RgbaImage Image { get; init; } = null!;
    public Silhouette Sil { get; init; } = null!;
    public string SourcePath { get; init; } = "";
    /// <summary>可选：只用于取色的图（例如 AI 生成的平涂像素风版本），尺寸可以不同，自身轮廓对齐。</summary>
    public ViewImage? ColorSource { get; set; }
    /// <summary>只约束形状、不参与取色（例如由正面轮廓推断出的对称侧面）。</summary>
    public bool ShapeOnly { get; set; }

    /// <summary>是否 AI 生成的图（本来就是平涂卡通，不需要去光影）。</summary>
    public bool IsAi { get; set; }

    private RgbaImage? _colorImage;
    /// <summary>取色用的图：去掉了外轮廓描边（照片模式下还会去光影）。</summary>
    public RgbaImage ColorImage => _colorImage ??= OutlineCleaner.Clean(Image, Sil);

    /// <summary>去光影平涂化，结果用于取色。keyColors 非空时以这些颜色为固定簇中心。</summary>
    public void FlattenWith(IReadOnlyList<Color.Rgb>? keyColors)
    {
        _colorImage = ColorFlatten.Flatten(Image, Sil, 24, keyColors).Image;
        _lines = null;
    }

    private (bool[] Dark, bool[] Skeleton)? _lines;
    /// <summary>内部深色线条及其骨架（在去描边后的取色图上提取）。</summary>
    public (bool[] Dark, bool[] Skeleton) Lines => _lines ??= LineArt.Extract(ColorImage, Sil);

    public static ViewImage Load(ViewKind kind, string path, double bgTolerance = 10)
    {
        var img = RgbaImage.Load(path);
        // 超大照片先缩，算法不需要那么高的分辨率
        int longSide = Math.Max(img.W, img.H);
        if (longSide > 1600)
        {
            double k = 1600.0 / longSide;
            img = img.Resized((int)(img.W * k), (int)(img.H * k));
        }
        return new ViewImage { Kind = kind, Image = img, Sil = Silhouette.FromImage(img, bgTolerance), SourcePath = path };
    }
}
