namespace AiVideoEditor.Core.Color;

/// <summary>
/// D069 §7 (format v26): a video clip's colour correction — eight sliders −100…+100, 0 neutral: Экспозиция, Яркость,
/// Контраст, Света, Тени, Насыщенность, Температура, Оттенок. One value per clip (no keyframes). <see cref="ColorAdjust"/>
/// turns it into the per-pixel function the Preview and the export both apply to the clip's decoded picture.
/// </summary>
public sealed record ColorCorrection(int Exposure, int Brightness, int Contrast, int Highlights, int Shadows, int Saturation,
    int Temperature, int Tint)
{
    public const int Min = -100;
    public const int Max = 100;

    public static readonly ColorCorrection Neutral = new(0, 0, 0, 0, 0, 0, 0, 0);

    public bool IsNeutral => this == Neutral;

    /// <summary>The eight values in the Inspector's order.</summary>
    public int[] Values => new[] { Exposure, Brightness, Contrast, Highlights, Shadows, Saturation, Temperature, Tint };

    /// <summary>True when every value lies in −100…+100.</summary>
    public bool IsValid => Values.All(v => v is >= Min and <= Max);

    /// <summary>The correction with each value clamped to −100…+100.</summary>
    public ColorCorrection Clamped() => FromValues(Values.Select(v => Math.Clamp(v, Min, Max)).ToArray());

    /// <summary>The correction of eight values in the Inspector's order (<see cref="Values"/>).</summary>
    public static ColorCorrection FromValues(IReadOnlyList<int> v)
    {
        if (v.Count != 8) throw new ArgumentException("Eight values are expected.", nameof(v));
        return new ColorCorrection(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
    }

    /// <summary>The correction with value <paramref name="index"/> (in <see cref="Values"/>' order) set.</summary>
    public ColorCorrection With(int index, int value)
    {
        var values = Values;
        values[index] = value;
        return FromValues(values);
    }
}
