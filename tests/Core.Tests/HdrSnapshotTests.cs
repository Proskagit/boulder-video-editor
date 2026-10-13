using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 18 Step 18.5 (D033 T3, T4): the playback snapshot — shared by the Preview, the Source viewer and the export —
/// carries each asset's HDR conversion; a background colour probe that changes it is a change of the asset state (the
/// Preview rebuilds and reopens the asset's decoders), never a presentation-only change.
/// </summary>
public class HdrSnapshotTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly MediaAsset _video;

    public HdrSnapshotTests()
    {
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        _project.Timeline.VideoTracks.Add(v1);
        _video = new MediaAsset
        {
            FilePath = @"C:\media\vivo.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), Width = 3840, Height = 2160, DisplayWidth = 3840, DisplayHeight = 2160,
                FrameRate = Rate, StartTime = MediaTime.Zero
            }
        };
        _project.MediaAssets.Add(_video);
        v1.Clips.Add(new VideoClip
        {
            MediaAssetId = _video.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate),
            SourceOut = MediaTime.FromFrame(50, Rate)
        });
    }

    private void ProbeFindsHlg()
    {
        var m = _video.Metadata!;
        (m.PixelFormat, m.BitDepth, m.ColorTransfer, m.ColorPrimaries, m.ColorMatrix, m.ColorRange) =
            ("yuv420p10le", 10, "arib-std-b67", "bt2020", "bt2020nc", "tv");
    }

    [Fact]
    public void The_snapshot_carries_the_HDR_conversion_of_an_HDR_asset_and_none_of_an_SDR_one()
    {
        Assert.Null(PlaybackSnapshotBuilder.Build(_project, 1).Assets[_video.Id].Hdr);

        ProbeFindsHlg();

        Assert.Equal(HdrConversion.Hlg(), PlaybackSnapshotBuilder.Build(_project, 2).Assets[_video.Id].Hdr);
        Assert.Equal(HdrConversion.Hlg(),
            PlaybackSnapshotBuilder.BuildSource(_video, Rate, 250, new FrameSize(1920, 1080), 3).Assets[_video.Id].Hdr);
    }

    [Fact]
    public void A_colour_probe_that_finds_HDR_changes_the_asset_state_and_is_not_presentation_only()
    {
        var states = PlaybackSnapshotBuilder.CaptureAssetStates(_project);
        var before = PlaybackSnapshotBuilder.Build(_project, 1);

        ProbeFindsHlg();

        Assert.True(PlaybackSnapshotBuilder.AssetStatesDiffer(states, PlaybackSnapshotBuilder.CaptureAssetStates(_project)));
        Assert.False(before.DiffersOnlyInPresentation(PlaybackSnapshotBuilder.Build(_project, 2)));
    }

    [Fact]
    public void A_colour_probe_that_finds_SDR_changes_nothing_for_playback()
    {
        var states = PlaybackSnapshotBuilder.CaptureAssetStates(_project);
        var before = PlaybackSnapshotBuilder.Build(_project, 1);

        (_video.Metadata!.PixelFormat, _video.Metadata.BitDepth) = ("yuv420p", 8);

        Assert.False(PlaybackSnapshotBuilder.AssetStatesDiffer(states, PlaybackSnapshotBuilder.CaptureAssetStates(_project)));
        Assert.True(before.DiffersOnlyInPresentation(PlaybackSnapshotBuilder.Build(_project, 2)));
    }
}
