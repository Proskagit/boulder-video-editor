using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Vision;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>Where the model and the E1 reference data are, and how they are read (frames and masks through ffmpeg).</summary>
internal static class ReferenceData
{
    public static string ModelDir =>
        Environment.GetEnvironmentVariable("AIVE_AI_MODEL_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiVideoEditor", "models", HoldModel.FolderName);

    public static string E1Dir =>
        Environment.GetEnvironmentVariable("AIVE_AI_E1_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "AiVideoEditor-E1-Reference");

    public static string Path_(string relative) => Path.Combine(E1Dir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The clicks of onnx_reference.json: clip, id, frame, point, the Python ONNX scores and chosen index.</summary>
    public static IReadOnlyList<RefClick> Clicks()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_("reference/onnx_reference.json")));
        return doc.RootElement.GetProperty("clicks").EnumerateArray().Select(c => new RefClick(
            c.GetProperty("clip").GetString()!, c.GetProperty("id").GetInt32(), c.GetProperty("frame").GetString()!,
            c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(),
            c.GetProperty("scores").EnumerateArray().Select(s => s.GetDouble()).ToArray(), c.GetProperty("chosen").GetInt32())).ToList();
    }

    /// <summary>The final E1 protocol (recheck/protocol_d034_behaviour.csv): per SDR click the mark source and final marks.</summary>
    public static IReadOnlyDictionary<(string Clip, int Id), RefMark> Marks()
    {
        var lines = File.ReadAllLines(Path_("recheck/protocol_d034_behaviour.csv"));
        var header = lines[0].Split(',');
        int Col(string n) => Array.IndexOf(header, n);
        return lines.Skip(1).Select(l => l.Split(',')).ToDictionary(
            f => (f[Col("clip")], int.Parse(f[Col("id")], CultureInfo.InvariantCulture)),
            f => new RefMark(f[Col("mark_source")], f[Col("segmentation_mark")], f[Col("d034_behaviour_mark")],
                bool.Parse(f[Col("all_masks_over_2pct")])));
    }

    public static DecodedFrame Frame(string relative)
    {
        var path = Path_(relative);
        var (w, h) = PngSize(path);
        var bytes = Ffmpeg(path, "bgra", w * h * 4);
        return new DecodedFrame(w, h, w * 4, bytes, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    /// <summary>A 1-bit / grey mask PNG as 0 / 1 per pixel.</summary>
    public static byte[] Mask(string relative)
    {
        var path = Path_(relative);
        var (w, h) = PngSize(path);
        var grey = Ffmpeg(path, "gray", w * h);
        for (var i = 0; i < grey.Length; i++) grey[i] = grey[i] > 127 ? (byte)1 : (byte)0;
        return grey;
    }

    public static double IoU(byte[] a, byte[] b)
    {
        long inter = 0, union = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != 0 && b[i] != 0) inter++;
            if (a[i] != 0 || b[i] != 0) union++;
        }
        return union == 0 ? 1.0 : (double)inter / union;
    }

    private static (int W, int H) PngSize(string path)
    {
        var header = new byte[24];
        using (var f = File.OpenRead(path)) f.ReadExactly(header);
        int Be(int o) => (header[o] << 24) | (header[o + 1] << 16) | (header[o + 2] << 8) | header[o + 3];
        return (Be(16), Be(20));
    }

    private static byte[] Ffmpeg(string path, string pixelFormat, int expected)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", pixelFormat, "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        copy.Wait();
        if (ms.Length != expected) throw new InvalidOperationException($"ffmpeg read {ms.Length} bytes of {path}, expected {expected}: {err.Result}");
        return ms.ToArray();
    }
}

internal sealed record RefClick(string Clip, int Id, string Frame, double X, double Y, double[] Scores, int Chosen);

internal sealed record RefMark(string Source, string SegmentationMark, string BehaviourMark, bool AllMasksOver2Pct);
