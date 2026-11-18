namespace AiVideoEditor.Core.Color;

/// <summary>
/// D069 §7: the colour correction as one pure per-pixel function of 8-bit BGRA pixels — the same code for the Preview and
/// the export (Preview = export). Everything but the saturation is a curve of each channel on its own, so it is a table of 256
/// values per channel; the saturation then mixes each pixel with its luma.
/// <list type="number">
/// <item>Exposure ±2 stops and the white balance (temperature: red against blue; tint: green against magenta), on linear light
/// (sRGB decoded), the balance gains normalised so the luminance of a grey stays.</item>
/// <item>Back to sRGB; brightness — a gamma on the midtones (the black and the white stay).</item>
/// <item>Shadows and highlights — a lift or a fall weighted to the dark or the bright values (0 and 1 stay).</item>
/// <item>Contrast — a stretch about the middle grey, clamped.</item>
/// <item>Saturation — the distance from the pixel's luma scaled by 0…2 (−100 grey, +100 double).</item>
/// </list>
/// Alpha is kept. A neutral correction changes nothing (and is not applied at all).
/// </summary>
public sealed class ColorAdjust
{
    private readonly byte[] _b = new byte[256], _g = new byte[256], _r = new byte[256];
    private readonly int _saturation256;

    public ColorCorrection Correction { get; }

    private ColorAdjust(ColorCorrection correction)
    {
        Correction = correction;
        var c = correction;
        var exposure = Math.Pow(2, c.Exposure / 100.0 * 2);
        var t = c.Temperature / 100.0;
        var tint = c.Tint / 100.0;
        double gainR = 1 + 0.3 * t, gainB = 1 - 0.3 * t, gainG = 1 - 0.25 * tint;
        var norm = 0.2126 * gainR + 0.7152 * gainG + 0.0722 * gainB;
        (gainR, gainG, gainB) = (gainR / norm, gainG / norm, gainB / norm);
        var gamma = Math.Pow(2, -0.8 * c.Brightness / 100.0);
        var shadows = c.Shadows / 100.0 * 0.6;
        var highlights = c.Highlights / 100.0 * 0.6;
        var contrast = c.Contrast >= 0 ? 1 + 0.8 * c.Contrast / 100.0 : 1 + 0.7 * c.Contrast / 100.0;
        for (var v = 0; v < 256; v++)
        {
            var linear = SrgbToLinear(v / 255.0) * exposure;
            _r[v] = Curve(linear * gainR);
            _g[v] = Curve(linear * gainG);
            _b[v] = Curve(linear * gainB);
        }
        _saturation256 = (int)Math.Round((1 + c.Saturation / 100.0) * 256);

        byte Curve(double linear)
        {
            var x = LinearToSrgb(Math.Clamp(linear, 0, 1));
            x = Math.Pow(x, gamma);
            x += shadows * Math.Sqrt(x) * (1 - x) * (1 - x);
            x += highlights * x * x * Math.Sqrt(Math.Max(0, 1 - x));
            x = 0.5 + (Math.Clamp(x, 0, 1) - 0.5) * contrast;
            return (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }
    }

    private static ColorAdjust? _last;

    /// <summary>The function of <paramref name="correction"/> (the last one is kept, so a frame after frame of one clip builds
    /// its tables once).</summary>
    public static ColorAdjust For(ColorCorrection correction)
    {
        var last = Volatile.Read(ref _last);
        if (last is not null && last.Correction == correction) return last;
        var made = new ColorAdjust(correction);
        Volatile.Write(ref _last, made);
        return made;
    }

    /// <summary>Corrects one row of BGRA pixels in place.</summary>
    public void ApplyRow(Span<byte> row)
    {
        var s = _saturation256;
        for (var i = 0; i + 3 < row.Length; i += 4)
        {
            int b = _b[row[i]], g = _g[row[i + 1]], r = _r[row[i + 2]];
            if (s != 256)
            {
                var luma = (54 * r + 183 * g + 19 * b) >> 8;
                b = Math.Clamp(luma + (((b - luma) * s) >> 8), 0, 255);
                g = Math.Clamp(luma + (((g - luma) * s) >> 8), 0, 255);
                r = Math.Clamp(luma + (((r - luma) * s) >> 8), 0, 255);
            }
            row[i] = (byte)b;
            row[i + 1] = (byte)g;
            row[i + 2] = (byte)r;
        }
    }

    /// <summary>Corrects <paramref name="height"/> rows of <paramref name="width"/> BGRA pixels, <paramref name="stride"/> bytes
    /// apart, in place — rows in parallel for a large picture.</summary>
    public void Apply(byte[] pixels, int offset, int width, int height, int stride)
    {
        var rowBytes = width * 4;
        if ((long)width * height < 200_000)
        {
            for (var y = 0; y < height; y++) ApplyRow(pixels.AsSpan(offset + y * stride, rowBytes));
            return;
        }
        Parallel.For(0, height, y => ApplyRow(pixels.AsSpan(offset + y * stride, rowBytes)));
    }

    public static double SrgbToLinear(double x) => x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
    public static double LinearToSrgb(double x) => x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
}
