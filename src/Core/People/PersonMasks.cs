using System.Collections.Immutable;
using System.IO.Compression;
using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.People;

/// <summary>
/// A coverage mask on the mask grid (D061 §4): <see cref="Width"/> × <see cref="Height"/> bytes, row by row, 0 — nobody,
/// 255 — a person, values between — the soft edge. The grid spans the display-oriented source frame (the space of the hold
/// contours), so the mask is drawn through the same layer transform as the frame and the contours. Treated as immutable.
/// </summary>
public sealed class OcclusionMask(int width, int height, byte[] coverage)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Coverage { get; } = coverage;

    /// <summary>The coverage at a normalized point (nearest cell); 0 outside the frame.</summary>
    public byte At(double x, double y)
    {
        var cx = (int)(x * Width);
        var cy = (int)(y * Height);
        return cx < 0 || cy < 0 || cx >= Width || cy >= Height ? (byte)0 : Coverage[cy * Width + cx];
    }
}

/// <summary>One person's mask on one analysed frame: the person's id (the same person keeps it from frame to frame), the
/// bounding rectangle of its coverage on the grid and the coverage inside it, deflated (row by row, <c>Width × Height</c>
/// bytes once inflated).</summary>
public sealed record PersonMaskEntry(int PersonId, int X, int Y, int Width, int Height, byte[] Deflated);

/// <summary>The masks of the people on one analysed frame at source time <see cref="Time"/>.</summary>
public sealed record PersonMaskSample(MediaTime Time, ImmutableArray<PersonMaskEntry> People);

/// <summary>
/// The people's masks of a video (D061 §4): every analysed frame (<see cref="Interval"/> apart, source time from the file's
/// start) with one separate mask per person — never several people in one mask, each mask the visible part of its person
/// only (where one person hides another, the pixel belongs to the one in front). Derived from the video alone, kept in the
/// application's cache like <see cref="PersonTracks"/>. Immutable; <see cref="CoverageAt"/> and <see cref="PeopleAt"/> are safe
/// from any thread (a small cache of inflated frames inside).
/// </summary>
public sealed class PersonMaskTrack
{
    /// <summary>Changes whenever the segmentation or the stored data change their meaning.</summary>
    public const int AnalysisVersion = 2;   // 2: one object per person (PersonRoots), fragments and duplicates suppressed

    private const int CacheSize = 24;
    private readonly object _gate = new();
    private readonly Dictionary<int, byte[]> _unions = new();
    private readonly Queue<int> _order = new();

    public PersonMaskTrack(int width, int height, MediaTime interval, ImmutableArray<PersonMaskSample> samples, string modelId)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The mask grid must have a positive size.");
        Width = width;
        Height = height;
        Interval = interval;
        Samples = samples;
        ModelId = modelId;
    }

    public int Width { get; }
    public int Height { get; }
    public MediaTime Interval { get; }
    public ImmutableArray<PersonMaskSample> Samples { get; }
    public string ModelId { get; }

    /// <summary>The ids of every person with a mask somewhere.</summary>
    public IEnumerable<int> PersonIds => Samples.SelectMany(s => s.People).Select(p => p.PersonId).Distinct().OrderBy(id => id);

    /// <summary>
    /// The coverage of all the people at <paramref name="time"/>: between two analysed frames at most two intervals apart
    /// the masks are blended linearly (no jumps between the analysed frames); near a single analysed frame (within one
    /// interval) its mask; null where nothing was analysed (no mask: the contours are drawn as they are).
    /// </summary>
    public OcclusionMask? CoverageAt(MediaTime time)
    {
        if (Samples.IsDefaultOrEmpty) return null;
        var after = FirstAtOrAfter(time);
        var before = after < Samples.Length && Samples[after].Time == time ? after : after - 1;
        if (before >= 0 && before == after) return new OcclusionMask(Width, Height, Union(before));
        var hasBefore = before >= 0;
        var hasAfter = after < Samples.Length;
        if (hasBefore && hasAfter && (Samples[after].Time - Samples[before].Time).Ticks <= 2 * Interval.Ticks + 1)
        {
            var (a, b) = (Union(before), Union(after));
            var f = (time - Samples[before].Time).Ticks / (double)(Samples[after].Time - Samples[before].Time).Ticks;
            var blended = new byte[a.Length];
            var wb = (int)Math.Round(f * 256);
            var wa = 256 - wb;
            for (var i = 0; i < blended.Length; i++) blended[i] = (byte)((a[i] * wa + b[i] * wb + 128) >> 8);
            return new OcclusionMask(Width, Height, blended);
        }
        var nearest = !hasAfter || (hasBefore && (time - Samples[before].Time).Ticks <= (Samples[after].Time - time).Ticks) ? before : after;
        return Math.Abs((Samples[nearest].Time - time).Ticks) <= Interval.Ticks ? new OcclusionMask(Width, Height, Union(nearest)) : null;
    }

    /// <summary>The separate masks of the people on the analysed frame nearest to <paramref name="time"/> (within one interval),
    /// each on the full grid; empty elsewhere. For showing the segmentation (the Preview's overlay).</summary>
    public IReadOnlyList<(int PersonId, OcclusionMask Mask)> PeopleAt(MediaTime time)
    {
        if (Samples.IsDefaultOrEmpty) return Array.Empty<(int, OcclusionMask)>();
        var after = FirstAtOrAfter(time);
        var nearest = after >= Samples.Length || (after > 0 && (time - Samples[after - 1].Time).Ticks <= (Samples[after].Time - time).Ticks)
            ? after - 1 : after;
        if (nearest < 0 || Math.Abs((Samples[nearest].Time - time).Ticks) > Interval.Ticks) return Array.Empty<(int, OcclusionMask)>();
        return Samples[nearest].People.Select(p =>
        {
            var grid = new byte[Width * Height];
            Paste(p, grid, Width);
            return (p.PersonId, new OcclusionMask(Width, Height, grid));
        }).ToList();
    }

    private int FirstAtOrAfter(MediaTime time)
    {
        int lo = 0, hi = Samples.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Samples[mid].Time < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The union of a sample's masks (cached).</summary>
    private byte[] Union(int index)
    {
        lock (_gate)
            if (_unions.TryGetValue(index, out var cached)) return cached;
        var grid = new byte[Width * Height];
        foreach (var person in Samples[index].People) Paste(person, grid, Width);
        lock (_gate)
        {
            if (_unions.TryAdd(index, grid))
            {
                _order.Enqueue(index);
                while (_order.Count > CacheSize) _unions.Remove(_order.Dequeue());
            }
            return _unions[index];
        }
    }

    /// <summary>Inflates one person's coverage into <paramref name="grid"/> (the maximum where it overlaps).</summary>
    private static void Paste(PersonMaskEntry person, byte[] grid, int gridWidth)
    {
        var crop = MaskCodec.Inflate(person.Deflated, person.Width * person.Height);
        for (var y = 0; y < person.Height; y++)
        {
            var row = (person.Y + y) * gridWidth + person.X;
            for (var x = 0; x < person.Width; x++)
            {
                var v = crop[y * person.Width + x];
                if (v > grid[row + x]) grid[row + x] = v;
            }
        }
    }
}

/// <summary>The masks' compression (D061 §4): the coverage inside a person's rectangle, deflated — random access per frame,
/// no video codec.</summary>
public static class MaskCodec
{
    public static byte[] Deflate(ReadOnlySpan<byte> coverage)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(coverage);
        return output.ToArray();
    }

    public static byte[] Inflate(byte[] deflated, int length)
    {
        var result = new byte[length];
        using var input = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        var read = 0;
        while (read < length)
        {
            var n = input.Read(result, read, length - read);
            if (n == 0) throw new InvalidDataException("A person mask is shorter than its rectangle.");
            read += n;
        }
        return result;
    }

    /// <summary>The entry of one person's coverage on the full grid: its bounding rectangle (of values above
    /// <paramref name="threshold"/>) and the coverage inside it; null when it covers nothing.</summary>
    public static PersonMaskEntry? Entry(int personId, byte[] grid, int width, int height, byte threshold = 8)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (grid[y * width + x] <= threshold) continue;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        if (x1 < 0) return null;
        var (w, h) = (x1 - x0 + 1, y1 - y0 + 1);
        var crop = new byte[w * h];
        for (var y = 0; y < h; y++)
            Array.Copy(grid, (y0 + y) * width + x0, crop, y * w, w);
        return new PersonMaskEntry(personId, x0, y0, w, h, Deflate(crop));
    }
}
