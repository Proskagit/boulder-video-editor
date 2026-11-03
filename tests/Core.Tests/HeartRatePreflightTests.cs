using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 35.5, D049 §10: a heart-rate overlay that reaches the output without data is a warning to confirm — no training, a
/// training not usable, or a video right below it whose recording zero is unknown (named with its clips); the export may
/// still start. A project without an overlay, or one outside the exported range, is never warned about, whatever its
/// training; an overlay with data everywhere gives no warning.
/// </summary>
public class HeartRatePreflightTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string Output = @"C:\Exports\out.mp4";
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    private static bool IsExportFolder(string path) => string.Equals(path, @"C:\Exports", StringComparison.OrdinalIgnoreCase);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };

    public HeartRatePreflightTests() => _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2 });

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private VideoClip Video(long start, long frames, DateTimeOffset? zero, string name)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{name}", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            RecordingZeroOverrideUtc = zero,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(600), Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080,
                FrameRate = Rate, StartTime = MediaTime.Zero
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(frames), SourceOut = F(frames) };
        _v1.Clips.Add(clip);
        _v1.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private TemplateClip Overlay(long start, long frames, TemplateKind kind = TemplateKind.HeartRate)
    {
        var clip = new TemplateClip { Kind = kind, TimelineStart = F(start), Duration = F(frames), AccentHex = TemplateRules.HeartRateAccentHex };
        _v2.Clips.Add(clip);
        _v2.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private void Train(TrainingAvailability availability = TrainingAvailability.Available)
    {
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS", Manufacturer = 294, Product = null, StartUtc = Start, EndUtc = Start.AddSeconds(600), UtcOffset = null,
            ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(600), Sport = 31, SubSport = 69,
            AverageHeartRate = null, MaxHeartRate = null,
            HeartRate = Enumerable.Range(0, 600).Select(i => new HeartRateSample(Start.AddSeconds(i), 120)).ToList(),
            Pauses = Array.Empty<WorkoutInterval>(), Segments = Array.Empty<WorkoutSegment>(), Warnings = Array.Empty<TrainingFileWarning>()
        };
        _project.Training = new TrainingReference
        {
            FilePath = @"C:\fit\training.fit", FileSizeBytes = 1, Sha256 = new string('a', 64), Activity = TrainingActivity.Of(workout),
            Workout = workout, Availability = availability
        };
    }

    private ExportPreflightResult Check(ExportRange? range = null) =>
        ExportPreflight.Check(_project, Output, new ExportPreflightEnvironment(true, _ => true, _ => true, IsExportFolder), range);

    private static IReadOnlyList<ExportIssue> HeartRateIssues(ExportPreflightResult result) =>
        result.Issues.Where(i => i.Kind == ExportIssueKind.HeartRateUnavailable).ToList();

    [Fact]
    public void An_overlay_with_data_everywhere_gives_no_warning()
    {
        Train();
        Video(0, 250, Start.AddSeconds(10), "a.mp4");
        Overlay(0, 250);
        var result = Check();
        Assert.True(result.CanExport);
        Assert.Empty(HeartRateIssues(result));
    }

    [Fact]
    public void Without_a_training_the_overlays_are_named_and_the_export_may_start()
    {
        Video(0, 250, Start.AddSeconds(10), "a.mp4");
        var first = Overlay(0, 100);
        var second = Overlay(150, 100);
        var result = Check();

        Assert.True(result.CanExport);
        var issue = Assert.Single(HeartRateIssues(result));
        Assert.Equal(ExportIssueSeverity.Warning, issue.Severity);
        Assert.Equal(new[] { first.Id, second.Id }, issue.ClipIds);
    }

    [Theory]
    [InlineData(TrainingAvailability.Missing)]
    [InlineData(TrainingAvailability.Unreadable)]
    [InlineData(TrainingAvailability.OtherActivity)]
    [InlineData(TrainingAvailability.Unresolved)]
    public void A_training_not_usable_is_a_warning_naming_its_file(TrainingAvailability availability)
    {
        Train(availability);
        Video(0, 250, Start.AddSeconds(10), "a.mp4");
        Overlay(0, 250);
        var issue = Assert.Single(HeartRateIssues(Check()));
        Assert.Equal(@"C:\fit\training.fit", issue.Subject);
    }

    [Fact]
    public void A_video_below_without_a_recording_zero_is_named_with_its_clips()
    {
        Train();
        Video(0, 100, Start.AddSeconds(10), "known.mp4");
        var unknownA = Video(100, 100, null, "unknown.mp4");
        Video(200, 100, Start.AddSeconds(300), "known2.mp4");
        var unknownB = new VideoClip { MediaAssetId = unknownA.MediaAssetId, TimelineStart = F(300), Duration = F(50), SourceIn = F(100), SourceOut = F(150) };
        _v1.Clips.Add(unknownB);
        Overlay(0, 350);

        var result = Check();
        Assert.True(result.CanExport);
        var issue = Assert.Single(HeartRateIssues(result));
        Assert.Equal(unknownA.MediaAssetId, issue.AssetId);
        Assert.Equal(new[] { unknownA.Id, unknownB.Id }, issue.ClipIds);
        Assert.Contains("unknown.mp4", issue.Message);
    }

    [Fact]
    public void A_video_hidden_under_another_picture_is_not_below_the_overlay()
    {
        var v3 = new Track { Type = TrackType.Video, Name = "V3", Order = 2 };
        _project.Timeline.VideoTracks.Add(v3);
        Train();
        Video(0, 100, null, "hidden.mp4");                         // V1, covered by the V2 video
        var asset = new MediaAsset
        {
            FilePath = @"C:\media\top.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            RecordingZeroOverrideUtc = Start.AddSeconds(10),
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = Rate }
        };
        _project.MediaAssets.Add(asset);
        _v2.Clips.Add(new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(0), Duration = F(100), SourceOut = F(100) });
        v3.Clips.Add(new TemplateClip { Kind = TemplateKind.HeartRate, TimelineStart = F(0), Duration = F(100), AccentHex = TemplateRules.HeartRateAccentHex });

        Assert.Empty(HeartRateIssues(Check()));
    }

    [Fact]
    public void No_overlay_or_one_outside_the_range_is_never_checked()
    {
        Video(0, 300, null, "a.mp4");
        Overlay(0, 100, TemplateKind.Rec);
        Assert.Empty(HeartRateIssues(Check()));                       // no training, no zero, no overlay: nothing

        Overlay(200, 100);
        Assert.Empty(HeartRateIssues(Check(new ExportRange(0, 150)))); // the overlay is outside the range
        Assert.Single(HeartRateIssues(Check(new ExportRange(0, 250))));
    }
}
