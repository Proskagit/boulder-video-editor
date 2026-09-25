using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 8 Step 1 (D023): the export preflight collects every problem at once; offline, not analysed
/// and unsupported media block, a missing font warns; only clips that reach the output are checked
/// (the same ones the snapshot plays); the job is the snapshot the checks were made on.
/// </summary>
public class ExportPreflightTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string Output = @"C:\Exports\out.mp4";

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1", Order = 0 };

    private readonly HashSet<string> _missingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase) { @"C:\Exports" };
    private readonly HashSet<string> _fonts = new(StringComparer.OrdinalIgnoreCase) { "Segoe UI", "Arial" };
    private bool _encoderAvailable = true;

    public ExportPreflightTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _project.Timeline.VideoTracks.Add(_v2);
        _project.Timeline.AudioTracks.Add(_a1);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(MediaKind kind, bool withAudio = true, MediaAnalysisStatus status = MediaAnalysisStatus.Completed)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.{(kind == MediaKind.Image ? "png" : "mp4")}",
            Kind = kind,
            AnalysisStatus = status,
            Metadata = status == MediaAnalysisStatus.Completed
                ? new MediaMetadata
                {
                    Duration = MediaTime.FromSeconds(60), Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080,
                    FrameRate = kind == MediaKind.Image ? null : Rate, StartTime = MediaTime.Zero,
                    AudioCodec = kind != MediaKind.Image && withAudio ? "aac" : null
                }
                : null
        };
        _project.MediaAssets.Add(asset);
        return asset;
    }

    private static T Add<T>(Track track, T clip, long startFrame, long endFrame) where T : Clip
    {
        clip.TimelineStart = F(startFrame);
        clip.Duration = F(endFrame) - F(startFrame);
        if (clip is MediaBackedClip media)
            media.SourceOut = media.SourceIn + clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private ExportPreflightResult Check(string? output = Output) =>
        ExportPreflight.Check(_project, output, new ExportPreflightEnvironment(
            _encoderAvailable, _fonts.Contains, path => !_missingFiles.Contains(path), _folders.Contains));

    private static ExportIssue Only(ExportPreflightResult result, ExportIssueKind kind) => Assert.Single(result.Issues, i => i.Kind == kind);

    // --- the job ----------------------------------------------------------------------------------------

    [Fact]
    public void A_valid_project_gets_a_job_on_the_snapshot_of_the_project()
    {
        var video = Asset(MediaKind.Video);
        var clip = Add(_v1, new VideoClip { MediaAssetId = video.Id, Opacity = 0.5 }, 0, 250);
        Add(_v2, new TextClip { Text = "Title" }, 50, 100);
        _project.Settings.FrameWidth = 1280;
        _project.Settings.FrameHeight = 720;

        var result = Check();

        Assert.True(result.CanExport);
        Assert.Empty(result.Issues);
        var job = result.Job!;
        Assert.Equal(Output, job.OutputPath);
        Assert.Equal(F(250), job.Snapshot.Duration);
        Assert.Equal(new Composition.FrameSize(1280, 720), job.Snapshot.Canvas);
        Assert.Equal(0.5, job.Snapshot.VideoLayers.Single(l => l.TrackId == _v1.Id).Spans.Single().Visual.Opacity);
        Assert.Equal(clip.Id, Assert.Single(job.Snapshot.AudioSpans).ClipId);
        Assert.Equal(250, job.Output.FrameCount);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_errors_before_warnings_and_there_is_no_job()
    {
        _project.Settings.FrameWidth = 1919;
        _encoderAvailable = false;
        var offline = Asset(MediaKind.Video);
        offline.IsMissing = true;
        Add(_v1, new VideoClip { MediaAssetId = offline.Id }, 0, 25);
        Add(_v2, new TextClip { Text = "Hi", FontFamily = "Nowhere Sans" }, 0, 25);

        var result = Check(@"C:\Nowhere\out.mp4");

        Assert.False(result.CanExport);
        Assert.Null(result.Job);
        Assert.Equal(
            new[] { ExportIssueKind.InvalidCanvas, ExportIssueKind.OutputFolderMissing, ExportIssueKind.EncoderUnavailable,
                    ExportIssueKind.MediaOffline, ExportIssueKind.FontMissing },
            result.Issues.Select(i => i.Kind));
        Assert.Equal(ExportIssueSeverity.Warning, result.Issues[^1].Severity);
        Assert.Equal(4, result.Errors.Count());
    }

    [Fact]
    public void An_empty_timeline_cannot_be_exported()
    {
        Assert.Equal(ExportIssueSeverity.Error, Only(Check(), ExportIssueKind.EmptyTimeline).Severity);
    }

    [Fact]
    public void A_timeline_with_only_hidden_or_muted_content_can_be_exported_as_black_and_silence()
    {
        Add(_v1, new ImageClip { MediaAssetId = Asset(MediaKind.Image).Id }, 0, 25);
        _v1.IsHidden = true;

        Assert.True(Check().CanExport);
    }

    // --- canvas and output path ------------------------------------------------------------------------

    [Theory]
    [InlineData(1921, 1080)]
    [InlineData(1920, 1081)]
    [InlineData(1, 1)]
    public void An_odd_canvas_blocks(int width, int height)
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);
        _project.Settings.FrameWidth = width;
        _project.Settings.FrameHeight = height;

        Assert.Contains($"{width} × {height}", Only(Check(), ExportIssueKind.InvalidCanvas).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"out.mp4")]
    [InlineData(@"Exports\out.mp4")]
    [InlineData(@"C:\Exports\out.mov")]
    [InlineData(@"C:\Exports\out")]
    [InlineData(@"C:\Exports\bad|name.mp4")]
    public void An_invalid_output_path_blocks(string? path)
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);

        Assert.Equal(ExportIssueSeverity.Error, Only(Check(path), ExportIssueKind.InvalidOutputPath).Severity);
    }

    [Fact]
    public void A_folder_named_like_the_output_blocks()
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);
        _folders.Add(@"C:\Exports\clip.mp4");

        Only(Check(@"C:\Exports\clip.mp4"), ExportIssueKind.InvalidOutputPath);
    }

    [Fact]
    public void The_output_path_is_normalized_and_the_extension_is_case_insensitive()
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);

        var result = Check(@"C:\Exports\sub\..\Out.MP4");

        Assert.Equal(@"C:\Exports\Out.MP4", result.Job!.OutputPath);
    }

    [Fact]
    public void A_missing_output_folder_blocks()
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);

        Assert.Contains(@"C:\Nowhere", Only(Check(@"C:\Nowhere\out.mp4"), ExportIssueKind.OutputFolderMissing).Message);
    }

    [Fact]
    public void The_output_must_not_overwrite_project_media_even_unused()
    {
        var media = Asset(MediaKind.Video);
        Add(_v2, new TextClip { Text = "x" }, 0, 25);
        _folders.Add(@"C:\media");

        var issue = Only(Check(media.FilePath.ToUpperInvariant()), ExportIssueKind.OutputIsProjectMedia);
        Assert.Equal(media.Id, issue.AssetId);
    }

    [Fact]
    public void An_unavailable_encoder_blocks()
    {
        Add(_v2, new TextClip { Text = "x" }, 0, 25);
        _encoderAvailable = false;

        Only(Check(), ExportIssueKind.EncoderUnavailable);
    }

    // --- media ------------------------------------------------------------------------------------------

    [Fact]
    public void Offline_media_blocks_marked_missing_vanished_since_open_or_not_in_the_project()
    {
        var missing = Asset(MediaKind.Video);
        missing.IsMissing = true;
        var vanished = Asset(MediaKind.Audio);
        _missingFiles.Add(vanished.FilePath);
        var a = Add(_v1, new VideoClip { MediaAssetId = missing.Id }, 0, 25);
        var b = Add(_a1, new AudioClip { MediaAssetId = vanished.Id }, 0, 25);
        var c = Add(_v2, new ImageClip { MediaAssetId = Guid.NewGuid() }, 0, 25);

        var issues = Check().Issues.Where(i => i.Kind == ExportIssueKind.MediaOffline).ToList();

        Assert.Equal(3, issues.Count);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }.Order(), issues.SelectMany(i => i.ClipIds).Order());
        Assert.Contains(issues, i => i.AssetId == missing.Id && i.Subject == missing.FilePath && i.Message.Contains(missing.FileName));
        Assert.Contains(issues, i => i.Message.Contains("not in the project"));
    }

    [Theory]
    [InlineData(MediaAnalysisStatus.Pending, "not been analysed yet")]
    [InlineData(MediaAnalysisStatus.Analyzing, "not been analysed yet")]
    [InlineData(MediaAnalysisStatus.Failed, "could not be analysed")]
    public void Media_without_completed_analysis_blocks_videos_and_images(MediaAnalysisStatus status, string text)
    {
        var video = Asset(MediaKind.Video, status: status);
        var image = Asset(MediaKind.Image, status: status);
        Add(_v1, new VideoClip { MediaAssetId = video.Id }, 0, 25);
        Add(_v2, new ImageClip { MediaAssetId = image.Id }, 0, 25);

        var issues = Check().Issues.Where(i => i.Kind == ExportIssueKind.MediaNotAnalyzed).ToList();

        Assert.Equal(new[] { video.Id, image.Id }.Order(), issues.Select(i => i.AssetId!.Value).Order());
        Assert.All(issues, i => Assert.Contains(text, i.Message));
    }

    [Fact]
    public void A_clip_that_cannot_play_its_media_blocks()
    {
        var image = Asset(MediaKind.Image);
        var clip = Add(_v1, new VideoClip { MediaAssetId = image.Id }, 0, 25);

        Assert.Equal(clip.Id, Assert.Single(Only(Check(), ExportIssueKind.MediaUnsupported).ClipIds));
    }

    [Fact]
    public void Clips_of_one_media_file_are_one_issue_listing_each_clip_once_in_timeline_order()
    {
        var media = Asset(MediaKind.Video);
        media.IsMissing = true;
        var late = Add(_v1, new VideoClip { MediaAssetId = media.Id }, 100, 125);   // picture + audio
        var early = Add(_v2, new VideoClip { MediaAssetId = media.Id }, 0, 25);

        var issue = Only(Check(), ExportIssueKind.MediaOffline);

        Assert.Equal(new[] { early.Id, late.Id }, issue.ClipIds);
        Assert.Contains("2 clips, first at 00:00:00.000", issue.Message);
    }

    [Fact]
    public void Clips_that_never_reach_the_output_are_not_checked()
    {
        var offline = Asset(MediaKind.Video);
        offline.IsMissing = true;
        var offlineImage = Asset(MediaKind.Image);
        offlineImage.IsMissing = true;
        var silent = Asset(MediaKind.Video, withAudio: false);
        silent.IsMissing = true;

        var hidden = new Track { Type = TrackType.Video, Name = "V3", Order = 2, IsHidden = true };
        _project.Timeline.VideoTracks.Add(hidden);
        Add(hidden, new ImageClip { MediaAssetId = offlineImage.Id }, 0, 25);                         // hidden picture, no audio
        Add(_v1, new VideoClip { MediaAssetId = silent.Id, Opacity = 0 }, 0, 25);                     // invisible, no audio
        Add(_v2, new VideoClip { MediaAssetId = offline.Id, Opacity = 0, IsMuted = true }, 0, 25);   // invisible, muted
        Add(_v2, new VideoClip { MediaAssetId = offline.Id, Opacity = 0, Volume = 0 }, 25, 50);      // invisible, volume 0
        var mutedTrack = new Track { Type = TrackType.Audio, Name = "A2", Order = 1, IsMuted = true };
        _project.Timeline.AudioTracks.Add(mutedTrack);
        Add(mutedTrack, new AudioClip { MediaAssetId = offline.Id }, 0, 25);                         // muted track

        var result = Check();

        Assert.True(result.CanExport);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Audio_that_is_heard_is_checked_even_when_its_picture_is_not_shown()
    {
        var offline = Asset(MediaKind.Video);
        offline.IsMissing = true;
        _v1.IsHidden = true;
        var hiddenPicture = Add(_v1, new VideoClip { MediaAssetId = offline.Id }, 0, 25);
        var transparent = Add(_v2, new VideoClip { MediaAssetId = offline.Id, Opacity = 0 }, 0, 25);

        Assert.Equal(new[] { hiddenPicture.Id, transparent.Id }.Order(), Only(Check(), ExportIssueKind.MediaOffline).ClipIds.Order());
    }

    // --- fonts ------------------------------------------------------------------------------------------

    [Fact]
    public void A_missing_font_warns_once_per_font_and_the_export_can_still_start()
    {
        var a = Add(_v1, new TextClip { Text = "A", FontFamily = "Nowhere Sans" }, 30, 40);
        var b = Add(_v2, new TextClip { Text = "B", FontFamily = "nowhere sans" }, 0, 10);
        Add(_v2, new TextClip { Text = "C", FontFamily = "arial" }, 20, 30);

        var result = Check();

        Assert.True(result.CanExport);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ExportIssueKind.FontMissing, warning.Kind);
        Assert.Equal(new[] { b.Id, a.Id }, warning.ClipIds);
        Assert.Contains("Nowhere Sans", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fonts_of_text_that_is_never_drawn_are_not_checked()
    {
        var hidden = new Track { Type = TrackType.Video, Name = "V3", Order = 2, IsHidden = true };
        _project.Timeline.VideoTracks.Add(hidden);
        Add(hidden, new TextClip { Text = "hidden", FontFamily = "Nowhere" }, 0, 10);
        Add(_v1, new TextClip { Text = "  \n ", FontFamily = "Nowhere" }, 0, 10);
        Add(_v2, new TextClip { Text = "clear", FontFamily = "Nowhere", Opacity = 0 }, 0, 10);

        Assert.Empty(Check().Issues);
    }
}
