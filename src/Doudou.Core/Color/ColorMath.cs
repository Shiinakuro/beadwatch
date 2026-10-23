namespace Doudou.Core.Color;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public uint Packed => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public double Luma => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }

    public Rgb Scale(double k) => new(Clamp(R * k), Clamp(G * k), Clamp(B * k));

    public static Rgb Lerp(Rgb a, Rgb b, double t) =>
        new(Clamp(a.R + (b.R - a.R) * t), Clamp(a.G + (b.G - a.G) * t), Clamp(a.B + (b.B - a.B) * t));

    public static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}

public readonly record struct Lab(double L, double A, double B);

/// <summary>sRGB ↔ CIELab 以及 CIEDE2000 色差。</summary>
public static class ColorMath
{
    private static readonly double[] SrgbToLinear = BuildLinearTable();

    private static double[] BuildLinearTable()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    public static Lab ToLab(Rgb c) => ToLab(c.R, c.G, c.B);

    public static Lab ToLab(double r255, double g255, double b255)
    {
        double r = Lin(r255), g = Lin(g255), b = Lin(b255);
        double x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        double y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        double z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
    }

    private static double Lin(double v255)
    {
        int i = (int)v255;
        if (i == v255 && i >= 0 && i < 256) return SrgbToLinear[i];
        double c = Math.Clamp(v255 / 255.0, 0, 1);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static Rgb FromLab(Lab lab)
    {
        double fy = (lab.L + 16) / 116, fx = fy + lab.A / 500, fz = fy - lab.B / 200;
        double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
        double r = x * 3.2404542 - y * 1.5371385 - z * 0.4985314;
        double g = -x * 0.9692660 + y * 1.8760108 + z * 0.0415560;
        double b = x * 0.0556434 - y * 0.2040259 + z * 1.0572252;
        return new Rgb(Gamma(r), Gamma(g), Gamma(b));

        static double Inv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
        static byte Gamma(double c)
        {
            c = Math.Clamp(c, 0, 1);
            double v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return Rgb.Clamp(v * 255);
        }
    }

    /// <summary>CIEDE2000 色差。</summary>
    public static double DeltaE2000(Lab l1, Lab l2)
    {
        const double kL = 1, kC = 1, kH = 1;
        double c1 = Math.Sqrt(l1.A * l1.A + l1.B * l1.B);
        double c2 = Math.Sqrt(l2.A * l2.A + l2.B * l2.B);
        double cBar = (c1 + c2) / 2;
        double cBar7 = Math.Pow(cBar, 7);
        double g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + 6103515625.0)));
        double a1p = (1 + g) * l1.A, a2p = (1 + g) * l2.A;
        double c1p = Math.Sqrt(a1p * a1p + l1.B * l1.B);
        double c2p = Math.Sqrt(a2p * a2p + l2.B * l2.B);
        double h1p = Hue(l1.B, a1p), h2p = Hue(l2.B, a2p);

        double dLp = l2.L - l1.L;
        double dCp = c2p - c1p;
        double dhp;
        if (c1p * c2p == 0) dhp = 0;
        else
        {
            dhp = h2p - h1p;
            if (dhp > 180) dhp -= 360;
            else if (dhp < -180) dhp += 360;
        }
        double dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dhp / 2));

        double lBarP = (l1.L + l2.L) / 2;
        double cBarP = (c1p + c2p) / 2;
        double hBarP;
        if (c1p * c2p == 0) hBarP = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180) hBarP = (h1p + h2p) / 2;
        else if (h1p + h2p < 360) hBarP = (h1p + h2p + 360) / 2;
        else hBarP = (h1p + h2p - 360) / 2;

        double t = 1 - 0.17 * Math.Cos(Rad(hBarP - 30)) + 0.24 * Math.Cos(Rad(2 * hBarP))
                   + 0.32 * Math.Cos(Rad(3 * hBarP + 6)) - 0.20 * Math.Cos(Rad(4 * hBarP - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hBarP - 275) / 25, 2));
        double cBarP7 = Math.Pow(cBarP, 7);
        double rc = 2 * Math.Sqrt(cBarP7 / (cBarP7 + 6103515625.0));
        double lb50 = (lBarP - 50) * (lBarP - 50);
        double sl = 1 + 0.015 * lb50 / Math.Sqrt(20 + lb50);
        double sc = 1 + 0.045 * cBarP;
        double sh = 1 + 0.015 * cBarP * t;
        double rt = -Math.Sin(Rad(2 * dTheta)) * rc;

        double vl = dLp / (kL * sl), vc = dCp / (kC * sc), vh = dHp / (kH * sh);
        return Math.Sqrt(vl * vl + vc * vc + vh * vh + rt * vc * vh);

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0) return 0;
            double h = Math.Atan2(b, a) * 180 / Math.PI;
            return h < 0 ? h + 360 : h;
        }
        static double Rad(double d) => d * Math.PI / 180;
    }

    public static double DeltaE(Rgb a, Rgb b) => DeltaE2000(ToLab(a), ToLab(b));
}
