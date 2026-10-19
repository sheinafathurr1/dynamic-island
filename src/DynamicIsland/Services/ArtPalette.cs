using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>Picks a vivid accent colour from album art to tint the equalizer, like iOS does.</summary>
internal static class ArtPalette
{
    public static Color Accent(BitmapSource tiny)
    {
        var bgra = new FormatConvertedBitmap(tiny, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double pb = pixels[i] / 255.0, pg = pixels[i + 1] / 255.0, pr = pixels[i + 2] / 255.0;
            double max = Math.Max(pr, Math.Max(pg, pb));
            double min = Math.Min(pr, Math.Min(pg, pb));
            double saturation = max <= 0 ? 0 : (max - min) / max;

            // Favour colourful, bright pixels over the dark/grey background most covers have.
            double weight = 0.04 + saturation * saturation * max;
            r += pr * weight;
            g += pg * weight;
            b += pb * weight;
            total += weight;
        }

        return total <= 0 ? MediaService.DefaultAccent : Vivid(r / total, g / total, b / total);
    }

    private static Color Vivid(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double l = (max + min) / 2, s = 0, h = 0;

        if (delta > 1e-6)
        {
            s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
            if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h /= 6;
        }

        // Black & white artwork gets a soft white instead of a muddy grey.
        if (s < 0.12) return MediaService.DefaultAccent;

        // Must read well on pure black.
        return FromHsl(h, Math.Max(s, 0.55), Math.Clamp(l, 0.58, 0.72));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromRgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;

        double value = t < 1.0 / 6 ? p + (q - p) * 6 * t
            : t < 0.5 ? q
            : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
            : p;
        return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}
