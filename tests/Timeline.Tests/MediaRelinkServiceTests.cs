using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D026 §3 (Step 11.4): <see cref="MediaRelinkService"/> — an offline asset gets another file, keeps its id and every
/// clip, as one undoable step; the hard rejects and warnings of PO-2 / PO-6 / PO-9 and the Step 11.4 refinements; PO-3
/// (ffprobe unavailable: allowed, Pending) apart from a probe that fails (rejected); Apply validates again what may have
/// changed since the check; a later analysis that finds the file too short is reported. The old files don't exist (the
/// assets are offline after the re-check); the new files are a fake file system, their probe a fake analysis.
/// </summary>
public sealed class MediaRelinkServiceTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TimelineFixture _f = new();
    private readonly FakeProbe _probe = new();
    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaRelinkService _relink;
    private readonly List<IReadOnlyList<MediaAsset>> _relinked = new();
    private readonly List<RelinkedMediaIncompatibleEventArgs> _incompatible = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-relink-tests", Guid.NewGuid().ToString("N"));

    public MediaRelinkServiceTests()
    {
        _f.Settings.FrameRate = Rate;
        _f.Settings.IsFrameRateLocked = true;
        _relink = new MediaRelinkService(_f.Projects, _f.UndoRedo, _probe, NullLogger<MediaRelinkService>.Instance,
            path => _files.TryGetValue(path, out var size) ? size : null);
        _f.Projects.MediaRelinked += (_, e) => _relinked.Add(e.Assets);
        _relink.RelinkedMediaFoundIncompatible += (_, e) => _incompatible.Add(e);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- fakes and helpers -------------------------------------------------------------------------------------------

    private sealed class FakeProbe : IMediaAnalysisService
    {
        public ConcurrentDictionary<string, MediaAnalysisResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MediaAnalysisResult? Default { get; set; }
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default)
        {
            Calls.Enqueue(filePath);
            return Task.FromResult(Results.TryGetValue(filePath, out var r) ? r
                : Default ?? MediaAnalysisResult.Failure(MediaAnalysisOutcome.FileNotFound, "not found"));
        }
    }

    private static MediaMetadata VideoMeta(double seconds, int width = 640, int height = 360, FrameRate? rate = null,
        string? audio = "aac") => new()
    {
        Duration = MediaTime.FromSeconds(seconds), Width = width, Height = height, DisplayWidth = width, DisplayHeight = height,
        DisplayRotation = 0, FrameRate = rate ?? Rate, AvgFrameRate = rate ?? Rate, StartTime = MediaTime.Zero,
        VideoCodec = "h264", AudioCodec = audio, AudioSampleRate = audio is null ? null : 48000, AudioChannels = audio is null ? null : 2
    };

    private static MediaMetadata AudioMeta(double seconds) => new()
    {
        Duration = MediaTime.FromSeconds(seconds), StartTime = MediaTime.Zero, AudioCodec = "pcm_s16le", AudioSampleRate = 48000, AudioChannels = 2
    };

    private static MediaMetadata ImageMeta(int width = 480, int height = 270) => new()
    {
        Width = width, Height = height, DisplayWidth = width, DisplayHeight = height, DisplayRotation = 0, VideoCodec = "png"
    };

    /// <summary>An analysed asset whose file is not there (offline after the re-check).</summary>
    private MediaAsset Offline(string name, MediaKind kind, MediaMetadata? metadata) =>
        _f.AddAsset(name, kind, metadata, metadata is null ? MediaAnalysisStatus.Pending : MediaAnalysisStatus.Completed);

    /// <summary>A file of the fake file system, probed as <paramref name="metadata"/>.</summary>
    private string NewFile(string name, MediaMetadata? metadata, long size = 1234)
    {
        var path = Path.Combine(_root, name);
        _files[path] = size;
        if (metadata is not null) _probe.Results[path] = MediaAnalysisResult.Success(metadata);
        return path;
    }

    private Clip AddClip(MediaAsset asset, Guid? track = null, double at = 0)
    {
        var result = _f.Service.AddClip(asset.Id, track, MediaTime.FromFrame((long)(at * 25), Rate));
        Assert.True(result.Success, result.Message);
        return _f.Project.Timeline.VideoTracks.Concat(_f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
    }

    private void Trim(Clip clip, double endSeconds) =>
        Assert.True(_f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromFrame((long)(endSeconds * 25), Rate)).Success);

    private async Task<RelinkResult> RelinkAsync(MediaAsset asset, string path)
    {
        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.True(check.CanApply, check.Message);
        return await _relink.ApplyAsync(check);
    }

    private async Task<RelinkCheck> Rejected(MediaAsset asset, string path, RelinkRejection expected)
    {
        await _f.Projects.RecheckMediaAsync();                             // the missing state Check will see (runtime)
        var snapshot = _f.Snapshot();
        var before = MediaFileState.Capture(asset);
        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.Equal(expected, check.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
        var apply = await _relink.ApplyAsync(check);                       // a rejected check is never applied
        Assert.False(apply.Applied);
        Assert.Equal(before, MediaFileState.Capture(asset));
        Assert.Equal(snapshot, _f.Snapshot());
        Assert.False(_f.UndoRedo.CanUndo && _f.UndoRedo.NextUndo is RelinkMediaCommand);
        return check;
    }

    // --- success, undo, redo ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_relink_keeps_the_asset_id_and_every_clip_and_gives_the_asset_the_new_file()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        Trim(clip, 4);
        Assert.True(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);
        var snapshot = _f.Snapshot();
        var metadata = VideoMeta(12);
        var path = NewFile("new.mp4", metadata, size: 5555);

        var result = await RelinkAsync(asset, path);

        Assert.True(result.Applied, result.Message);
        Assert.Same(asset, _f.Project.MediaAssets.Single(a => a.Id == asset.Id));
        Assert.Equal(path, asset.FilePath);
        Assert.Equal(5555, asset.FileSizeBytes);
        Assert.Same(metadata, asset.Metadata);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Null(asset.AnalysisError);
        Assert.False(asset.IsMissing);
        Assert.Equal(snapshot, _f.Snapshot());                              // clips, speed, fades, dissolves untouched
        Assert.Equal(asset.Id, ((MediaBackedClip)clip).MediaAssetId);
        Assert.True(_f.Project.IsDirty);
        Assert.IsType<RelinkMediaCommand>(_f.UndoRedo.NextUndo);
        Assert.Same(asset, Assert.Single(Assert.Single(_relinked)));
        _f.AssertValid();
    }

    [Fact]
    public async Task Undo_and_redo_restore_the_asset_exactly()
    {
        var oldMetadata = VideoMeta(10);
        var asset = Offline("old.mp4", MediaKind.Video, oldMetadata);
        asset.FileSizeBytes = 777;
        AddClip(asset);
        await _f.Projects.SaveAsAsync(Path.Combine(_root, "Project"));     // clean
        await _f.Projects.RecheckMediaAsync();
        var before = MediaFileState.Capture(asset);
        Assert.True(before.IsMissing);
        var snapshot = _f.Snapshot();
        var path = NewFile("new.mp4", VideoMeta(12), 999);
        await RelinkAsync(asset, path);
        var after = MediaFileState.Capture(asset);

        _f.UndoRedo.Undo();
        Assert.Equal(before, MediaFileState.Capture(asset));
        Assert.Same(oldMetadata, asset.Metadata);
        Assert.False(_f.Project.IsDirty);                                   // back at the save point
        Assert.Equal(snapshot, _f.Snapshot());

        _f.UndoRedo.Redo();
        Assert.Equal(after, MediaFileState.Capture(asset));
        Assert.True(_f.Project.IsDirty);
        Assert.Equal(3, _relinked.Count);                                   // relink, undo, redo — each told
    }

    [Fact]
    public async Task A_relinked_project_saves_and_reopens_with_the_new_path()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        AddClip(asset);
        var folder = Path.Combine(_root, "Project");
        await _f.Projects.SaveAsAsync(folder);
        var media = Path.Combine(folder, "media", "new.mp4");               // inside the project: a relative path too
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        File.WriteAllBytes(media, new byte[321]);
        _files[media] = 321;
        _probe.Results[media] = MediaAnalysisResult.Success(VideoMeta(12));
        await RelinkAsync(asset, media);

        await _f.Projects.SaveAsync();
        var json = File.ReadAllText(Path.Combine(folder, "project.json"));
        Assert.Contains("\"relativePath\": \"media\\\\new.mp4\"", json);
        var reopened = await _f.Projects.OpenAsync(folder);

        var again = reopened.MediaAssets.Single(a => a.Id == asset.Id);
        Assert.Equal(media, again.FilePath);
        Assert.False(again.IsMissing);
        Assert.Equal(MediaTime.FromSeconds(12), again.Metadata!.Duration);
        Assert.Equal(asset.Id, ((MediaBackedClip)reopened.Timeline.VideoTracks[0].Clips.Single()).MediaAssetId);
    }

    // --- hard rejects -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_online_asset_is_not_relinked()
    {
        var file = Path.Combine(_root, "here.mp4");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(file, new byte[10]);
        var asset = _f.AddAsset("here.mp4", MediaKind.Video, VideoMeta(10));
        asset.FilePath = file;
        asset.IsMissing = true;                                             // stale: the re-check of Check sees it

        await Rejected(asset, NewFile("new.mp4", VideoMeta(10)), RelinkRejection.NotOffline);
        Assert.False(asset.IsMissing);
    }

    [Fact]
    public async Task A_file_that_does_not_exist_is_rejected()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        await Rejected(asset, Path.Combine(_root, "nowhere.mp4"), RelinkRejection.FileNotFound);
    }

    [Theory]
    [InlineData("new.wav")]
    [InlineData("new.png")]
    [InlineData("new.txt")]
    public async Task A_file_of_another_type_is_rejected(string name)
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        await Rejected(asset, NewFile(name, VideoMeta(10)), RelinkRejection.WrongMediaType);
        Assert.Empty(_probe.Calls);                                         // not even probed
    }

    [Fact]
    public async Task A_video_file_without_a_video_stream_is_rejected()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var soundOnly = AudioMeta(10);
        var check = await Rejected(asset, NewFile("sound.mp4", soundOnly), RelinkRejection.WrongMediaType);
        Assert.Contains("no video stream", check.Message);
    }

    [Fact]
    public async Task An_audio_file_without_an_audio_stream_is_rejected()
    {
        var asset = Offline("old.wav", MediaKind.Audio, AudioMeta(10));
        await Rejected(asset, NewFile("new.m4a", new MediaMetadata { Duration = MediaTime.FromSeconds(10) }), RelinkRejection.WrongMediaType);
    }

    [Theory]
    [InlineData(MediaAnalysisOutcome.InvalidMedia)]
    [InlineData(MediaAnalysisOutcome.UnsupportedMedia)]
    [InlineData(MediaAnalysisOutcome.ProbeProcessFailed)]
    [InlineData(MediaAnalysisOutcome.InvalidOutput)]
    public async Task A_file_ffprobe_cannot_read_is_rejected(MediaAnalysisOutcome outcome)
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var path = NewFile("broken.mp4", null);
        _probe.Results[path] = MediaAnalysisResult.Failure(outcome, "Could not read the file.");

        await Rejected(asset, path, RelinkRejection.UnreadableMedia);
    }

    [Fact]
    public async Task A_file_shorter_than_the_source_range_in_use_is_rejected_and_one_exactly_long_enough_is_not()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        Trim(clip, 6);                                                      // SourceOut = 6 s

        var check = await Rejected(asset, NewFile("short.mp4", VideoMeta(5.96)), RelinkRejection.TooShort);
        Assert.Contains("0:00:06.000", check.Message);

        var exact = await RelinkAsync(asset, NewFile("exact.mp4", VideoMeta(6)));
        Assert.True(exact.Applied);
        _f.AssertValid();
    }

    [Fact]
    public async Task The_longest_source_range_of_every_clip_on_every_track_counts()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var first = AddClip(asset);
        Trim(first, 2);
        Assert.True(_f.Service.AddTrack(TrackType.Video).Success);
        var v2 = _f.Project.Timeline.VideoTracks[1];
        var second = AddClip(asset, v2.Id, at: 3);                          // 0–10 s of the source
        Trim(second, 3 + 8);                                                // SourceOut = 8 s

        await Rejected(asset, NewFile("seven.mp4", VideoMeta(7.5)), RelinkRejection.TooShort);
        Assert.True((await RelinkAsync(asset, NewFile("eight.mp4", VideoMeta(8)))).Applied);
    }

    [Fact]
    public async Task A_clip_at_another_speed_is_measured_by_its_source_range()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        Trim(clip, 3);
        Assert.True(_f.Service.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(40)).Success);   // 3 s of source in 1.5 s
        var sourceOut = ((MediaBackedClip)clip).SourceOut;
        Assert.Equal(MediaTime.FromSeconds(3), sourceOut);

        await Rejected(asset, NewFile("short.mp4", VideoMeta(2.5)), RelinkRejection.TooShort);
        Assert.True((await RelinkAsync(asset, NewFile("ok.mp4", VideoMeta(3)))).Applied);
    }

    [Fact]
    public async Task Media_without_clips_and_images_have_no_length_to_fit()
    {
        var unused = Offline("unused.mp4", MediaKind.Video, VideoMeta(10));
        Assert.True((await RelinkAsync(unused, NewFile("tiny.mp4", VideoMeta(0.5)))).Applied);

        var image = Offline("logo.png", MediaKind.Image, ImageMeta());
        AddClip(image);
        Assert.True((await RelinkAsync(image, NewFile("logo2.png", ImageMeta()))).Applied);
    }

    [Fact]
    public async Task A_path_that_belongs_to_another_asset_is_rejected_whatever_its_case()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var path = NewFile("taken.mp4", VideoMeta(10));
        var other = _f.AddAsset("taken.mp4", MediaKind.Video, VideoMeta(10));
        other.FilePath = path.ToUpperInvariant();

        var check = await Rejected(asset, path, RelinkRejection.PathInUse);
        Assert.Contains("already in the project", check.Message);
        Assert.Equal(2, _f.Project.MediaAssets.Count);                    // nothing merged
    }

    // --- warnings ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Differing_characteristics_are_warned_about_and_nothing_is_adapted()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        var snapshot = _f.Snapshot();
        var now = VideoMeta(10, 1920, 1080, FrameRate.Fps30, audio: null);
        now.DisplayRotation = 90;
        now.StartTime = MediaTime.FromSeconds(1);
        now.VideoCodec = "hevc";
        var path = NewFile("new.mp4", now);

        var check = await _relink.CheckAsync(asset.Id, path);

        Assert.True(check.CanApply);
        Assert.Equal(
            new[] { RelinkWarningKind.DisplaySize, RelinkWarningKind.FrameRate, RelinkWarningKind.NoAudio, RelinkWarningKind.Rotation,
                    RelinkWarningKind.StartTime, RelinkWarningKind.VideoCodec },
            check.Warnings.Select(w => w.Kind));
        Assert.All(check.Warnings, w => Assert.False(string.IsNullOrWhiteSpace(w.Message)));
        Assert.True((await _relink.ApplyAsync(check)).Applied);
        Assert.Equal(snapshot, _f.Snapshot());
        Assert.Equal(Rate, _f.Settings.FrameRate);                          // D007: the project rate stays
    }

    [Fact]
    public async Task Differing_audio_characteristics_are_warned_about()
    {
        var asset = Offline("old.wav", MediaKind.Audio, AudioMeta(10));
        AddClip(asset);
        var now = AudioMeta(10);
        now.AudioCodec = "mp3";
        now.AudioSampleRate = 44100;
        now.AudioChannels = 1;

        var check = await _relink.CheckAsync(asset.Id, NewFile("new.mp3", now));

        Assert.Equal(new[] { RelinkWarningKind.AudioCodec, RelinkWarningKind.SampleRate, RelinkWarningKind.Channels },
            check.Warnings.Select(w => w.Kind));
    }

    [Fact]
    public async Task The_same_characteristics_or_no_earlier_metadata_give_no_warning_and_a_longer_file_neither()
    {
        var same = Offline("same.mp4", MediaKind.Video, VideoMeta(10));
        AddClip(same);
        Assert.Empty((await _relink.CheckAsync(same.Id, NewFile("same2.mp4", VideoMeta(20)))).Warnings);

        var unknown = Offline("unknown.mp4", MediaKind.Video, null);        // never analysed
        Assert.Empty((await _relink.CheckAsync(unknown.Id, NewFile("any.mp4", VideoMeta(5, 1920, 1080, FrameRate.Fps30)))).Warnings);
    }

    [Fact]
    public async Task A_file_too_short_for_a_dissolve_is_warned_about()
    {
        var a = Offline("a.mp4", MediaKind.Video, VideoMeta(10));
        var b = Offline("b.mp4", MediaKind.Video, VideoMeta(10));
        var left = AddClip(a);
        Trim(left, 2);                                                      // A: source 0–2 s
        var right = AddClip(b, at: 2);
        Assert.True(_f.Service.TrimClip(right.Id, ClipEdge.Start, MediaTime.FromFrame(65, Rate)).Success);  // B: source from 0.6 s
        Assert.True(_f.Service.MoveClips(new[] { right.Id }, -15).Success);                                   // back to the cut
        Assert.True(_f.Service.AddTransition(left.Id, right.Id, MediaTime.FromFrame(24, Rate)).Success);  // A needs 12 frames after

        var check = await _relink.CheckAsync(a.Id, NewFile("a2.mp4", VideoMeta(2.2)));   // 5 frames beyond the clip

        Assert.True(check.CanApply);
        Assert.Equal(RelinkWarningKind.DissolveHandles, Assert.Single(check.Warnings).Kind);
        Assert.Empty((await _relink.CheckAsync(a.Id, NewFile("a3.mp4", VideoMeta(3)))).Warnings);
    }

    // --- ffprobe unavailable (PO-3) ------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_ffprobe_the_relink_is_allowed_with_a_warning_and_the_asset_is_left_unanalysed()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        AddClip(asset);
        var path = NewFile("new.mp4", null);
        _probe.Default = MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "ffprobe could not be found.");

        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.True(check.CanApply, check.Message);
        Assert.Equal(RelinkWarningKind.NotChecked, Assert.Single(check.Warnings).Kind);
        Assert.Null(check.Metadata);

        Assert.True((await _relink.ApplyAsync(check)).Applied);
        Assert.Equal(path, asset.FilePath);
        Assert.Null(asset.Metadata);                                        // not the old file's metadata
        Assert.Equal(MediaAnalysisStatus.Pending, asset.AnalysisStatus);
        Assert.False(asset.IsMissing);
        Assert.NotNull(TimelineValidator.ValidateSequence(_f.Project.Timeline, Rate, _f.FindAsset));  // edits wait for the analysis

        _f.UndoRedo.Undo();
        Assert.Equal(MediaTime.FromSeconds(10), asset.Metadata!.Duration);
    }

    [Fact]
    public async Task A_later_analysis_that_finds_the_file_too_short_is_reported_once_and_nothing_is_undone()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        Trim(clip, 6);
        _probe.Default = MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "ffprobe could not be found.");
        var path = NewFile("new.mp4", null);
        await RelinkAsync(asset, path);
        Assert.Empty(_incompatible);

        asset.Metadata = VideoMeta(4);                                      // the analysis at a later Open
        asset.AnalysisStatus = MediaAnalysisStatus.Completed;
        _f.Projects.NotifyMediaAssetsChanged();
        _f.Projects.NotifyMediaAssetsChanged();

        var report = Assert.Single(_incompatible);
        Assert.Same(asset, report.Asset);
        Assert.Contains("0:00:06.000", report.Message);
        Assert.Equal(path, asset.FilePath);                                 // kept; the user may Undo
        Assert.IsType<RelinkMediaCommand>(_f.UndoRedo.NextUndo);
    }

    [Fact]
    public async Task Media_that_fits_its_clips_is_never_reported()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        AddClip(asset);
        await RelinkAsync(asset, NewFile("new.mp4", VideoMeta(12)));
        _f.Projects.NotifyMediaAssetsChanged();

        Assert.Empty(_incompatible);
    }

    // --- Apply validates again ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_refuses_a_path_another_asset_took_meanwhile()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var path = NewFile("new.mp4", VideoMeta(10));
        var check = await _relink.CheckAsync(asset.Id, path);
        var other = _f.AddAsset("x.mp4", MediaKind.Video, VideoMeta(10));
        other.FilePath = path;

        var result = await _relink.ApplyAsync(check);

        Assert.Equal(RelinkRejection.PathInUse, result.Rejection);
        Assert.False(_f.UndoRedo.CanUndo);
    }

    [Fact]
    public async Task Apply_refuses_a_file_that_is_gone_or_changed_since_the_check()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var path = NewFile("new.mp4", VideoMeta(10), size: 100);
        var check = await _relink.CheckAsync(asset.Id, path);

        _files[path] = 200;
        Assert.Equal(RelinkRejection.Stale, (await _relink.ApplyAsync(check)).Rejection);
        _files.TryRemove(path, out _);
        Assert.Equal(RelinkRejection.FileNotFound, (await _relink.ApplyAsync(check)).Rejection);
        Assert.False(_f.UndoRedo.CanUndo);
    }

    [Fact]
    public async Task Apply_refuses_when_the_old_file_came_back_meanwhile()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var check = await _relink.CheckAsync(asset.Id, NewFile("new.mp4", VideoMeta(10)));
        Directory.CreateDirectory(Path.GetDirectoryName(asset.FilePath)!);
        File.WriteAllBytes(asset.FilePath, new byte[1]);
        try
        {
            Assert.Equal(RelinkRejection.NotOffline, (await _relink.ApplyAsync(check)).Rejection);
        }
        finally
        {
            File.Delete(asset.FilePath);
        }
    }

    [Fact]
    public async Task Apply_refuses_when_a_clip_was_lengthened_beyond_the_new_file_meanwhile()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var clip = AddClip(asset);
        Trim(clip, 4);
        var check = await _relink.CheckAsync(asset.Id, NewFile("new.mp4", VideoMeta(5)));
        Assert.True(check.CanApply);

        Trim(clip, 8);                                                      // the old metadata (10 s) allows it

        Assert.Equal(RelinkRejection.TooShort, (await _relink.ApplyAsync(check)).Rejection);
    }

    [Fact]
    public async Task Apply_refuses_a_check_made_for_another_project()
    {
        var asset = Offline("old.mp4", MediaKind.Video, VideoMeta(10));
        var check = await _relink.CheckAsync(asset.Id, NewFile("new.mp4", VideoMeta(10)));
        _f.Projects.CreateNew("Other");

        Assert.Equal(RelinkRejection.Stale, (await _relink.ApplyAsync(check)).Rejection);
    }

    [Fact]
    public async Task An_unknown_asset_is_rejected()
    {
        var check = await _relink.CheckAsync(Guid.NewGuid(), NewFile("new.mp4", VideoMeta(10)));
        Assert.Equal(RelinkRejection.AssetNotFound, check.Rejection);
    }
}
