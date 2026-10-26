// Hold tracking (R2 → E): runs the product's camera-motion estimate (Core/Motion) on a real clip exactly as the app's
// CameraMotionCoordinator does — the app's ffmpeg decoder, software, 960 px, a sample every 0.1 s — and writes the track
// and the per-frame diagnostics as JSON for the report and for the overlay check (overlay_track.py).
//
// Usage: dotnet run -c Release -- <clip.mp4> <reference_s> <start_s> <end_s> <out.json>
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: <clip.mp4> <reference_s> <start_s> <end_s> <out.json>");
    return 2;
}
var path = args[0];
double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
var reference = MediaTime.FromSeconds(D(args[1]));
var start = MediaTime.FromSeconds(D(args[2]));
var end = MediaTime.FromSeconds(D(args[3]));
const int analysisWidth = 960;
var interval = MediaTime.FromSeconds(0.1);

var locator = new FfmpegLocator(Options.Create(new FfmpegOptions()), NullLogger<FfmpegLocator>.Instance);
var decoder = new FfmpegVideoDecoder(locator, NullLogger<FfmpegVideoDecoder>.Instance);
VideoDecodeRequest Request(MediaTime at) => new()
{
    FilePath = path, StartTime = MediaTime.Zero, FirstSamplePoint = new SourceSamplePoint(at.Ticks, 1),
    MaxWidth = analysisWidth, MaxHeight = analysisWidth, Hardware = HardwareDecoding.Disabled
};

var total = Stopwatch.StartNew();
GrayImage referenceImage;
await using (var stream = await decoder.OpenAsync(Request(reference)))
{
    var point = new SourceSamplePoint(reference.Ticks, 1);
    DecodedFrame? current = null;
    GrayImage? gray = null;
    while (await stream.ReadFrameAsync() is { } frame)
    {
        if (current is not null && !SourceFrameSelector.IsAtOrBefore(frame.Timestamp, MediaTime.Zero, point)) break;
        current = frame;
        gray = GrayImage.FromBgra(frame);
    }
    referenceImage = gray ?? throw new InvalidOperationException("no reference frame");
}

var estimator = new CameraMotionEstimator(referenceImage, reference);
var estimateTime = Stopwatch.StartNew();
estimateTime.Stop();
var frames = 0;
await using (var stream = await decoder.OpenAsync(Request(start)))
{
    var next = start;
    while (await stream.ReadFrameAsync() is { } frame)
    {
        var time = CameraMotionMath.SourceTimeOf(frame.Timestamp, MediaTime.Zero);
        if (time > end + interval) break;
        if (time < next) continue;
        var gray = GrayImage.FromBgra(frame);
        estimateTime.Start();
        estimator.Add(gray, time);
        estimateTime.Stop();
        frames++;
        next = time + interval;
    }
}
var track = estimator.Build();
total.Stop();

var scale = 1920.0 / estimator.Width;
var result = new
{
    clip = Path.GetFileName(path),
    reference_s = reference.TotalSeconds, start_s = start.TotalSeconds, end_s = end.TotalSeconds,
    analysis = new[] { estimator.Width, estimator.Height },
    corners = estimator.CornerCount,
    model = track.Model.ToString(),
    samples = track.Samples.Length,
    unreliable = track.Samples.Count(s => !s.IsReliable),
    frames,
    total_s = Math.Round(total.Elapsed.TotalSeconds, 2),
    estimate_ms_per_frame = Math.Round(estimateTime.Elapsed.TotalMilliseconds / Math.Max(1, frames), 1),
    worst_similarity_median_1920 = Math.Round(estimator.Frames.Where(f => f.Fits).Select(f => f.SimilarityMedian * scale).DefaultIfEmpty(0).Max(), 2),
    worst_similarity_p95_1920 = Math.Round(estimator.Frames.Where(f => f.Fits).Select(f => f.SimilarityP95 * scale).DefaultIfEmpty(0).Max(), 2),
    worst_homography_median_1920 = Math.Round(estimator.Frames.Where(f => f.Fits).Select(f => f.HomographyMedian * scale).DefaultIfEmpty(0).Max(), 2),
    worst_homography_p95_1920 = Math.Round(estimator.Frames.Where(f => f.Fits).Select(f => f.HomographyP95 * scale).DefaultIfEmpty(0).Max(), 2),
    // How far the camera moved: the centre of the reference frame on each frame, in 1920-wide pixels.
    max_centre_shift_1920 = Math.Round(track.Samples.Where(s => s.IsReliable).Select(s =>
    {
        var (x, y) = s.ReferenceToFrame.Apply(0.5, 0.5);
        return Math.Sqrt((x - 0.5) * 1920 * (x - 0.5) * 1920 + (y - 0.5) * 1920 * estimator.Height / estimator.Width * ((y - 0.5) * 1920 * estimator.Height / estimator.Width));
    }).DefaultIfEmpty(0).Max(), 1),
    track = track.Samples.Select(s => new
    {
        t = s.SourceTime.TotalSeconds, reliable = s.IsReliable,
        m = new[] { s.ReferenceToFrame.M00, s.ReferenceToFrame.M01, s.ReferenceToFrame.M02, s.ReferenceToFrame.M10, s.ReferenceToFrame.M11,
            s.ReferenceToFrame.M12, s.ReferenceToFrame.M20, s.ReferenceToFrame.M21, s.ReferenceToFrame.M22 }
    }),
    diagnostics = estimator.Frames.Select(f => new
    {
        t = f.SourceTime.TotalSeconds, f.Fits, f.Tracked, f.Inliers,
        sim = new[] { Math.Round(f.SimilarityMedian * scale, 2), Math.Round(f.SimilarityP95 * scale, 2) },
        hom = new[] { Math.Round(f.HomographyMedian * scale, 2), Math.Round(f.HomographyP95 * scale, 2) }
    })
};
File.WriteAllText(args[4], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
Console.WriteLine($"{result.clip}: {result.model}, {result.samples} samples, {result.unreliable} unreliable, " +
                  $"sim {result.worst_similarity_median_1920}/{result.worst_similarity_p95_1920}, hom {result.worst_homography_median_1920}/{result.worst_homography_p95_1920}, " +
                  $"shift ≤ {result.max_centre_shift_1920} px, {result.estimate_ms_per_frame} ms/frame, {result.total_s} s");
return 0;
