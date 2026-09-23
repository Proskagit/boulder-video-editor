using System.Globalization;

namespace AiVideoEditor.Video;

/// <summary>What a file says about how its frames are to be shown, before interpretation.</summary>
/// <param name="RotationDegrees">ffprobe's <c>rotation</c> of the display matrix (counter-clockwise
/// degrees), or a legacy <c>rotate</c> tag converted to the same convention.</param>
/// <param name="IsMirrored">True when the display matrix flips the picture (negative determinant).</param>
internal readonly record struct OrientationHint(double RotationDegrees, bool IsMirrored);

/// <summary>Display orientation and the size of the frames the decoder delivers.</summary>
/// <param name="Rotation">Clockwise 0/90/180/270, or null when not a plain right-angle rotation.</param>
internal readonly record struct DisplayOrientationResult(int? Rotation, int DisplayWidth, int DisplayHeight, string? Unsupported);

/// <summary>
/// Interprets display-matrix orientation exactly as ffmpeg's automatic rotation does (the decoder
/// relies on it, so the display size must match the frames it really produces):
/// <list type="bullet">
/// <item>θ = −rotation, normalized to [0, 360);</item>
/// <item>within 1° of 90° or 270° ffmpeg transposes — width and height swap (also when mirrored);</item>
/// <item>within 1° of 0° or 180° the size is unchanged;</item>
/// <item>any other angle ("odd rotation") is applied by ffmpeg's rotate filter, which keeps the
/// coded size.</item>
/// </list>
/// Only unmirrored right angles count as a supported orientation; the rest keep a correct display
/// size but no <see cref="DisplayOrientationResult.Rotation"/> (reported through
/// <see cref="DisplayOrientationResult.Unsupported"/> for a diagnostic log).
/// </summary>
internal static class DisplayOrientation
{
    private const double Tolerance = 1.0; // degrees, as in ffmpeg's autorotate

    public static DisplayOrientationResult Resolve(int codedWidth, int codedHeight, OrientationHint? hint)
    {
        if (hint is not { } h || !double.IsFinite(h.RotationDegrees))
            return new DisplayOrientationResult(0, codedWidth, codedHeight, null);

        var theta = -h.RotationDegrees;
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);   // ffmpeg's normalization to ~[0, 360)
        var quarter = Math.Round(theta / 90);
        var nearRightAngle = Math.Abs(theta - quarter * 90) < Tolerance;
        var turns = (int)(((long)quarter % 4 + 4) % 4);
        var swap = nearRightAngle && turns % 2 == 1;
        var (width, height) = swap ? (codedHeight, codedWidth) : (codedWidth, codedHeight);

        if (h.IsMirrored)
            return new DisplayOrientationResult(null, width, height, "mirrored display matrix");
        if (!nearRightAngle)
            return new DisplayOrientationResult(null, width, height,
                $"rotation of {h.RotationDegrees.ToString("0.###", CultureInfo.InvariantCulture)}° is not a right angle");
        return new DisplayOrientationResult(turns * 90, width, height, null);
    }

    /// <summary>
    /// Reads a display matrix as ffprobe prints it ("00000000: a b u\n00000001: c d v\n…") and tells
    /// whether it mirrors the picture (determinant of the 2×2 part &lt; 0). Null when unreadable.
    /// </summary>
    public static bool? IsMirrored(string? displayMatrix)
    {
        if (string.IsNullOrWhiteSpace(displayMatrix)) return null;
        var values = new List<long>(9);
        foreach (var line in displayMatrix.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            foreach (var token in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
                values.Add(value);
            }
        }
        if (values.Count != 9) return null;
        var (a, b, c, d) = (values[0], values[1], values[3], values[4]);
        return (Int128)a * d - (Int128)b * c < 0;
    }
}
