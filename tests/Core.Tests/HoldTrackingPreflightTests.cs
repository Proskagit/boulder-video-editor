using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Hold tracking (R2 → E) in the export preflight: the job's snapshot carries the same camera-motion tracks as the
/// Preview; a clip whose contours reach the output while its motion is still analysed blocks the export (so the export
/// never draws the contours elsewhere than the Preview); clips without contours, or outside the range, never wait.
/// </summary>
public class HoldTrackingPreflightTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string Output = @"C:\Exports\out.mp4";
    private static bool IsExportFolder(string path) => string.Equals(path, @"C:\Exports", StringComparison.OrdinalIgnoreCase);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };

    public HoldTrackingPreflightTests() => _project.Timeline.VideoTracks.Add(_v1);

    private VideoClip Clip(long start, long end, bool withHolds)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080,
                FrameRate = Rate, StartTime = MediaTime.Zero
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromFrame(start, Rate), Duration = MediaTime.FromFrame(end - start, Rate),
            HoldMarkup = withHolds
                ? RouteColor.WithResolved(new HoldMarkup
                {
                    Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(),
                        ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
                        RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero))
                })
                : null
        };
        clip.SourceOut = clip.Duration;
        _v1.Clips.Add(clip);
        _v1.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private ExportPreflightResult Check(ExportCameraMotion motion, ExportRange? range = null) =>
        ExportPreflight.Check(_project, Output, new ExportPreflightEnvironment(true, _ => true, _ => true, IsExportFolder) { CameraMotion = motion }, range);

    private static CameraMotionTrack Track() =>
        new(MediaTime.Zero, CameraMotionModel.Similarity, ImmutableArray.Create(new CameraMotionSample(MediaTime.Zero, WallTransform.Identity, true)));

    [Fact]
    public void The_job_carries_the_previews_tracks()
    {
        var clip = Clip(0, 100, withHolds: true);
        var track = Track();
        var result = Check(new ExportCameraMotion(new Dictionary<Guid, CameraMotionTrack> { [clip.Id] = track }, new HashSet<Guid>()));
        Assert.True(result.CanExport);
        Assert.Same(track, result.Job!.Snapshot.VideoLayers.Single().Spans.Single().Outline!.Motion);
    }

    [Fact]
    public void A_clip_still_analysed_blocks_the_export()
    {
        var clip = Clip(0, 100, withHolds: true);
        var result = Check(new ExportCameraMotion(new Dictionary<Guid, CameraMotionTrack>(), new HashSet<Guid> { clip.Id }));
        Assert.False(result.CanExport);
        var issue = Assert.Single(result.Issues, i => i.Kind == ExportIssueKind.HoldTrackingPending);
        Assert.Equal(ExportIssueSeverity.Error, issue.Severity);
        Assert.Equal(new[] { clip.Id }, issue.ClipIds.ToArray());
    }

    [Fact]
    public void Clips_without_contours_or_outside_the_range_never_wait()
    {
        var plain = Clip(0, 100, withHolds: false);
        var later = Clip(100, 200, withHolds: true);
        var pending = new HashSet<Guid> { plain.Id, later.Id };
        Assert.True(Check(new ExportCameraMotion(new Dictionary<Guid, CameraMotionTrack>(), pending), new ExportRange(0, 50)).CanExport);
        Assert.False(Check(new ExportCameraMotion(new Dictionary<Guid, CameraMotionTrack>(), pending)).CanExport);
    }

    [Fact]
    public void Without_tracking_data_the_preflight_is_as_before()
    {
        Clip(0, 100, withHolds: true);
        var result = ExportPreflight.Check(_project, Output, new ExportPreflightEnvironment(true, _ => true, _ => true, IsExportFolder));
        Assert.True(result.CanExport);
        Assert.Null(result.Job!.Snapshot.VideoLayers.Single().Spans.Single().Outline!.Motion);
    }
}
