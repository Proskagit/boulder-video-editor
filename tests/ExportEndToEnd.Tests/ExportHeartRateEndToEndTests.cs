using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 35.5 through the real pipeline: the heart-rate overlay over a video with a recording zero is in the exported file —
/// drawn on every frame with a value and on none in the timer's pause (those frames equal the export without the overlay);
/// the Preview's own pipeline draws the sampled frames byte for byte like the export, horizontal and vertical; without a
/// training the overlay draws nothing anywhere and the export is the export without it.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportHeartRateEndToEndTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private readonly E2EMedia _media;

    public ExportHeartRateEndToEndTests(E2EMedia media) => _media = media;

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    /// <summary>A sample every second (95, 130, 140, 99 BPM) and the timer paused over the third second.</summary>
    private static TrainingReference Training()
    {
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = 814, StartUtc = T0, EndUtc = T0.AddSeconds(10),
            UtcOffset = null, ElapsedTime = MediaTime.FromSeconds(10), TimerTime = MediaTime.FromSeconds(9), Sport = 31, SubSport = 69,
            AverageHeartRate = null, MaxHeartRate = null,
            HeartRate = new[] { 95, 130, 140, 99, 101 }.Select((bpm, i) => new HeartRateSample(T0.AddSeconds(i), bpm)).ToList(),
            Pauses = new[] { new WorkoutInterval(T0.AddSeconds(2), T0.AddSeconds(3)) },
            Segments = Array.Empty<WorkoutSegment>(), Warnings = Array.Empty<TrainingFileWarning>()
        };
        return new TrainingReference
        {
            FilePath = @"C:\fit\e2e.fit", FileSizeBytes = 1, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = TrainingAvailability.Available
        };
    }

    /// <summary>The pattern on V1 (frames 0–100; its file's time — the tag read as the end — at the training's start, D055 §1;
    /// a recording zero 30 s off, which the heart rate never reads), the overlay over all of it on V2 — or, with
    /// <paramref name="area"/>, a green route area («Боулдер» anchored to the clip, D055 §4) instead.</summary>
    private ProjectBuilder Build(bool overlay, bool training, int width = E2EMedia.Width, int height = E2EMedia.Height,
        HeartRateLook look = HeartRateLook.Plate, bool area = false, bool areaHeartRate = true)
    {
        var p = new ProjectBuilder(FrameRate.Fps25, width, height);
        var clip = p.Video(p.VideoTrack(), _media.PatternWithTone(), 0, 100);
        var asset = p.Project.MediaAssets.Single(a => a.Id == clip.MediaAssetId);
        asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(
            T0.AddTicks(asset.Metadata.Duration.Ticks).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), RecordingTimeTagOrigin.Format);
        asset.RecordingZeroOverrideUtc = T0.AddSeconds(30);
        var v2 = p.VideoTrack();
        if (overlay) p.Template(v2, TemplateKind.HeartRate, 0, 100).Look = look;
        if (area)
        {
            var green = p.Template(v2, TemplateKind.Boulder, 0, 100);
            (green.AnchorClipId, green.Route, green.ShowHeartRate, green.Look) = (clip.Id, "7", areaHeartRate, look);
        }
        if (training) p.Project.Training = Training();
        return p;
    }

    [FfmpegFact]
    public async Task A_green_route_area_draws_its_clips_heart_rate_like_the_preview_and_nothing_in_the_pause()
    {
        var p = Build(overlay: false, training: true, area: true);
        var with = await Export(p, "heart-rate-area");
        var without = await Export(Build(overlay: false, training: true, area: true, areaHeartRate: false), "heart-rate-area-off");
        AssertValidMp4(with);
        for (var n = 1; n < 100; n++)
        {
            // 50–74: the timer's pause — the area's card stays, its heart rate is not drawn.
            var nothing = n is >= 50 and < 75;
            Assert.True(nothing == with.Canvases[n].AsSpan().SequenceEqual(without.Canvases[n]), $"frame {n}");
        }

        var snapshot = PlaybackSnapshotBuilder.Build(p.Project, 1);
        foreach (var n in new long[] { 12, 30, 55, 80 })
        {
            var preview = await Preview(snapshot, n);
            Assert.True(preview.Pixels.AsSpan().SequenceEqual(with.Canvases[(int)n]), $"frame {n} differs from the export");
        }
    }

    [FfmpegFact]
    public async Task The_overlay_is_in_the_file_and_nothing_is_drawn_in_the_pause()
    {
        var with = await Export(Build(overlay: true, training: true), "heart-rate");
        var without = await Export(Build(overlay: false, training: true), "heart-rate-none");
        AssertValidMp4(with);
        Assert.True(with.Pcm.AsSpan().SequenceEqual(without.Pcm), "the overlay changed the audio");

        for (var n = 0; n < 100; n++)
        {
            // Frame 0 is the first frame of the appearance (its opacity 0, as for every template); 50–74 the timer's pause.
            var nothing = n == 0 || n is >= 50 and < 75;
            Assert.True(nothing == with.Canvases[n].AsSpan().SequenceEqual(without.Canvases[n]), $"frame {n}");
        }
    }

    [FfmpegFact]
    public async Task Without_a_training_the_overlay_draws_nothing()
    {
        var with = await Export(Build(overlay: true, training: false), "heart-rate-no-training");
        var without = await Export(Build(overlay: false, training: false), "heart-rate-no-training-none");
        for (var n = 0; n < 100; n++)
            Assert.True(with.Canvases[n].AsSpan().SequenceEqual(without.Canvases[n]), $"frame {n} drew an overlay without data");
    }

    public static TheoryData<int, int, HeartRateLook> Cases => new()
    {
        { E2EMedia.Width, E2EMedia.Height, HeartRateLook.Plate },
        { E2EMedia.Height, E2EMedia.Width, HeartRateLook.Plain }
    };

    [FfmpegTheory]
    [MemberData(nameof(Cases))]
    public async Task The_preview_draws_it_like_the_export(int width, int height, HeartRateLook look)
    {
        var p = Build(overlay: true, training: true, width, height, look);
        var run = await Export(p, $"heart-rate-parity-{width}x{height}");
        AssertValidMp4(run);

        var snapshot = PlaybackSnapshotBuilder.Build(p.Project, 1);
        foreach (var n in new long[] { 3, 12, 30, 49, 55, 80, 99 })
        {
            var preview = await Preview(snapshot, n);
            Assert.True(preview.Pixels.AsSpan().SequenceEqual(run.Canvases[(int)n]), $"frame {n} differs from the export");
        }
    }
}
