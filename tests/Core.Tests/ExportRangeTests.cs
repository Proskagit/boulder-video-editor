using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 15 Step 15.7 (D030 §8, Q10 / Q16): an export of the In / Out range. Its output is the frames <c>[In, Out)</c>
/// and the timeline samples of their edges by the <c>AudioPlacement</c> rule; the preflight clamps the range to the
/// sequence, refuses an empty one, and checks only the media that reach the range — a picture shown in it (its dissolve
/// zones included), a sound placed in it, a text in it; every other check and the whole-sequence export are unchanged.
/// </summary>
public class ExportRangeTests
{
    private static readonly FrameRate Rate = FrameRate.Ntsc30;
    private const string Output = @"C:\Exports\out.mp4";

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1", Order = 0 };
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public ExportRangeTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _project.Timeline.AudioTracks.Add(_a1);
    }

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private MediaAsset Asset(MediaKind kind)
    {
        var audioOnly = kind == MediaKind.Audio;
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.{(audioOnly ? "wav" : "mp4")}",
            Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), Width = audioOnly ? null : 1920, Height = audioOnly ? null : 1080,
                DisplayWidth = audioOnly ? null : 1920, DisplayHeight = audioOnly ? null : 1080,
                FrameRate = audioOnly ? null : Rate, StartTime = MediaTime.Zero, AudioCodec = "aac", AudioSampleRate = 48000, AudioChannels = 2
            }
        };
        _project.MediaAssets.Add(asset);
        return asset;
    }

    private static T Add<T>(Track track, T clip, long start, long end) where T : Clip
    {
        clip.TimelineStart = F(start);
        clip.Duration = F(end) - F(start);
        if (clip is MediaBackedClip media) media.SourceOut = media.SourceIn + clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private ExportPreflightResult Check(ExportRange? range) =>
        ExportPreflight.Check(_project, Output, new ExportPreflightEnvironment(true, _ => true, p => !_missing.Contains(p), IsFolder), range);

    private static bool IsFolder(string path) => string.Equals(path, @"C:\Exports", StringComparison.OrdinalIgnoreCase);

    // --- the output -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_range_output_is_its_frames_and_the_samples_of_their_edges()
    {
        Add(_v1, new VideoClip { MediaAssetId = Asset(MediaKind.Video).Id }, 0, 300);
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 0);

        var whole = ExportOutput.For(snapshot);
        var range = ExportOutput.For(snapshot, new ExportRange(31, 95));

        Assert.Equal((300L, 0L, 0L), (whole.FrameCount, whole.FirstFrame, whole.FirstSample));
        Assert.Equal(64, range.FrameCount);
        Assert.Equal(31, range.FirstFrame);
        Assert.Equal(F(95) - F(31), range.Duration);
        var first = AudioTiming.CeilingSample(F(31));
        Assert.Equal(first, range.FirstSample);                                         // ⌈In · 48000 / 10⁷⌉
        Assert.Equal(AudioTiming.CeilingSample(F(95)) - first, range.AudioSampleCount);
        Assert.Equal(49_650, range.FirstSample);                                         // 31 · 1001 / 30000 s · 48000 = 49649.6 → 49650

        var low = ExportOutput.For(snapshot, new ExportRange(2, 10));                    // 3203.2: rounding up, not to the nearest
        Assert.Equal(3_204, low.FirstSample);
        Assert.Equal(AudioTiming.CeilingSample(F(10)) - 3_204, low.AudioSampleCount);
    }

    [Fact]
    public void Without_a_range_the_job_and_its_output_are_as_before()
    {
        Add(_v1, new VideoClip { MediaAssetId = Asset(MediaKind.Video).Id }, 0, 300);
        var job = Check(null).Job!;
        Assert.Null(job.Range);
        Assert.Equal(ExportOutput.For(job.Snapshot), job.Output);
    }

    // --- the preflight --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_range_is_clamped_to_the_sequence_and_an_empty_one_is_refused()
    {
        Add(_v1, new VideoClip { MediaAssetId = Asset(MediaKind.Video).Id }, 0, 300);

        Assert.Equal(new ExportRange(250, 300), Check(new ExportRange(250, 9000)).Job!.Range);
        var empty = Check(new ExportRange(300, 400));
        Assert.False(empty.CanExport);
        Assert.Equal("The In / Out range has no part of the timeline in it.",
            Assert.Single(empty.Errors, i => i.Kind == ExportIssueKind.EmptyRange).Message);
    }

    [Fact]
    public void Media_used_only_outside_the_range_does_not_block_media_inside_it_still_does()
    {
        var inside = Asset(MediaKind.Video);
        var outside = Asset(MediaKind.Video);
        var music = Asset(MediaKind.Audio);
        Add(_v1, new VideoClip { MediaAssetId = inside.Id }, 0, 100);
        Add(_v1, new VideoClip { MediaAssetId = outside.Id }, 100, 200);
        Add(_a1, new AudioClip { MediaAssetId = music.Id }, 150, 300);
        _missing.Add(outside.FilePath);
        _missing.Add(music.FilePath);

        Assert.False(Check(null).CanExport);                                            // the whole sequence: blocked
        var range = Check(new ExportRange(10, 100));                                    // ends where 'outside' starts
        Assert.True(range.CanExport, string.Join("; ", range.Errors.Select(e => e.Message)));
        Assert.Equal(new ExportRange(10, 100), range.Job!.Range);

        var touching = Check(new ExportRange(10, 101));                                 // one frame of 'outside'
        Assert.Equal(outside.Id, Assert.Single(touching.Errors).AssetId);

        _missing.Remove(outside.FilePath);
        var sound = Check(new ExportRange(140, 151));                                   // the music starts in it
        Assert.Equal(music.Id, Assert.Single(sound.Errors).AssetId);
    }

    [Fact]
    public void The_sound_counts_where_it_is_placed_by_the_sample_rule()
    {
        var music = Asset(MediaKind.Audio);
        Add(_v1, new TextClip { Text = "x" }, 0, 300);
        Add(_a1, new AudioClip { MediaAssetId = music.Id }, 100, 300);
        _missing.Add(music.FilePath);

        Assert.True(Check(new ExportRange(0, 100)).CanExport);                          // the sound starts at the range's end
        Assert.False(Check(new ExportRange(0, 101)).CanExport);
    }

    [Fact]
    public void A_picture_shown_in_the_range_through_a_dissolve_zone_counts()
    {
        var a = Asset(MediaKind.Video);
        var b = Asset(MediaKind.Video);
        var clipA = Add(_v1, new VideoClip { MediaAssetId = a.Id }, 0, 100);
        var clipB = Add(_v1, new VideoClip { MediaAssetId = b.Id, SourceIn = F(30) }, 100, 200);
        _v1.Transitions.Add(new Transition
        {
            TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(20), LeftClipId = clipA.Id, RightClipId = clipB.Id
        });
        _missing.Add(b.FilePath);

        Assert.True(Check(new ExportRange(0, 90)).CanExport);                           // before B's part of the zone
        Assert.Equal(b.Id, Assert.Single(Check(new ExportRange(0, 91)).Errors).AssetId); // B shows from frame 90
    }

    [Fact]
    public void A_missing_font_warns_only_for_a_text_in_the_range()
    {
        Add(_v1, new TextClip { Text = "x", FontFamily = "Nowhere Sans" }, 200, 300);
        Add(_a1, new AudioClip { MediaAssetId = Asset(MediaKind.Audio).Id }, 0, 300);
        var env = new ExportPreflightEnvironment(true, f => f != "Nowhere Sans", _ => true, IsFolder);

        Assert.Empty(ExportPreflight.Check(_project, Output, env, new ExportRange(0, 200)).Warnings);
        Assert.Single(ExportPreflight.Check(_project, Output, env, new ExportRange(0, 201)).Warnings);
    }
}
