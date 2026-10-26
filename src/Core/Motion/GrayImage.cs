using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Motion;

/// <summary>A single-channel image (luma 0..255 as floats) for the camera-motion estimate. Immutable after creation.</summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, float[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length != width * height) throw new ArgumentException("The pixels don't match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }

    public float this[int x, int y] => Pixels[y * Width + x];

    /// <summary>The BT.601 luma of a BGRA frame.</summary>
    public static GrayImage FromBgra(DecodedFrame frame)
    {
        var pixels = new float[frame.Width * frame.Height];
        var source = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            var row = source.Slice(y * frame.Stride, frame.Width * DecodedFrame.BytesPerPixel);
            for (var x = 0; x < frame.Width; x++)
            {
                var o = x * DecodedFrame.BytesPerPixel;
                pixels[y * frame.Width + x] = 0.114f * row[o] + 0.587f * row[o + 1] + 0.299f * row[o + 2];
            }
        }
        return new GrayImage(frame.Width, frame.Height, pixels);
    }

    /// <summary>Half the size (2 × 2 mean; an odd last row / column dropped).</summary>
    public GrayImage Downsample()
    {
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
        var pixels = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            int y0 = Math.Min(2 * y, Height - 1), y1 = Math.Min(2 * y + 1, Height - 1);
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Min(2 * x, Width - 1), x1 = Math.Min(2 * x + 1, Width - 1);
                pixels[y * w + x] = 0.25f * (this[x0, y0] + this[x1, y0] + this[x0, y1] + this[x1, y1]);
            }
        }
        return new GrayImage(w, h, pixels);
    }

    /// <summary>Bilinear sample; coordinates are clamped to the image.</summary>
    public float Sample(double x, double y)
    {
        x = Math.Clamp(x, 0, Width - 1.000001);
        y = Math.Clamp(y, 0, Height - 1.000001);
        int x0 = (int)x, y0 = (int)y;
        float fx = (float)(x - x0), fy = (float)(y - y0);
        var i = y0 * Width + x0;
        var top = Pixels[i] + (Pixels[i + 1] - Pixels[i]) * fx;
        var bottom = Pixels[i + Width] + (Pixels[i + Width + 1] - Pixels[i + Width]) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>A pyramid: this image, then halves, at most <paramref name="levels"/> levels, none smaller than 32 px.</summary>
    public GrayImage[] Pyramid(int levels)
    {
        var list = new List<GrayImage> { this };
        while (list.Count < levels && list[^1].Width >= 64 && list[^1].Height >= 64)
            list.Add(list[^1].Downsample());
        return list.ToArray();
    }
}
