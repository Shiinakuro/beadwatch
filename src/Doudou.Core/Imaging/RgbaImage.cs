using Doudou.Core.Color;
using SkiaSharp;

namespace Doudou.Core.Imaging;

/// <summary>简单的 RGBA 位图（非预乘），便于逐像素算法。</summary>
public sealed class RgbaImage
{
    public int W { get; }
    public int H { get; }
    public byte[] Px { get; }   // RGBA

    public RgbaImage(int w, int h, byte[]? px = null)
    {
        W = w; H = h;
        Px = px ?? new byte[w * h * 4];
    }

    public static RgbaImage Load(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("无法读取图片：" + path);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        codec.GetPixels(info, bmp.GetPixels());
        // 处理 EXIF 方向（手机照片常见）
        var oriented = ApplyOrigin(bmp, codec.EncodedOrigin);
        var img = new RgbaImage(oriented.Width, oriented.Height);
        System.Runtime.InteropServices.Marshal.Copy(oriented.GetPixels(), img.Px, 0, img.Px.Length);
        if (!ReferenceEquals(oriented, bmp)) oriented.Dispose();
        return img;
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var dst = new SKBitmap(info);
        using var c = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, src.Width / 2f, src.Height / 2f); break;
            case SKEncodedOrigin.RightTop: c.Translate(dst.Width, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, dst.Height); c.RotateDegrees(270); break;
            case SKEncodedOrigin.TopRight: c.Scale(-1, 1, src.Width / 2f, 0); break;
            case SKEncodedOrigin.BottomLeft: c.Scale(1, -1, 0, src.Height / 2f); break;
        }
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    public SKBitmap ToSkBitmap()
    {
        var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(Px, 0, bmp.GetPixels(), Px.Length);
        return bmp;
    }

    public void SavePng(string path)
    {
        using var bmp = ToSkBitmap();
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
    }

    public Rgb GetRgb(int x, int y)
    {
        int o = (y * W + x) * 4;
        return new Rgb(Px[o], Px[o + 1], Px[o + 2]);
    }

    public byte Alpha(int x, int y) => Px[(y * W + x) * 4 + 3];

    public RgbaImage Resized(int w, int h)
    {
        using var src = ToSkBitmap();
        using var dst = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
        var img = new RgbaImage(w, h);
        System.Runtime.InteropServices.Marshal.Copy(dst.GetPixels(), img.Px, 0, img.Px.Length);
        return img;
    }

    public RgbaImage Crop(int x0, int y0, int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x + x0, sy = y + y0;
                if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                Buffer.BlockCopy(Px, (sy * W + sx) * 4, img.Px, (y * w + x) * 4, 4);
            }
        return img;
    }

    public RgbaImage FlippedX()
    {
        var img = new RgbaImage(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                Buffer.BlockCopy(Px, (y * W + x) * 4, img.Px, (y * W + (W - 1 - x)) * 4, 4);
        return img;
    }
}
