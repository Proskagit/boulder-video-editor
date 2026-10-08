using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 15 Step 15.6 (D030 §7, Q12): slip. Only the clip's source mapping changes: <c>SourceIn</c> and <c>SourceOut</c>
/// move together by D022's start-trim amount — 1× <c>FromFrame(S + k) − FromFrame(S)</c>, another speed
/// <c>±SourceLength(|k|)</c> — while its start, end, duration, speed, fades and dissolves, the other clips, gaps, markers,
/// track flags, the sequence length and the playhead stay. Clamped to the source and to a dissolve's handle, with a
/// message; one undo step; nothing for 0 frames; images, text, an unknown length and a locked track refused.
/// </summary>
public class SlipTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    private static readonly FrameRate[] Rates = { FrameRate.Ntsc24, FrameRate.Fps25, FrameRate.Ntsc30, FrameRate.Fps30, FrameRate.Fps60 };

    public static TheoryData<int, int> RatesAndSpeeds()
    {
        var data = new TheoryData<int, int>();
        for (var rate = 0; rate < Rates.Length; rate++)
            foreach (var steps in new[] { 5, 10, 20, 40, 80 })
                data.Add(rate, steps);
        return data;
    }

    /// <summary>Everything a slip must leave as it is: every clip's timing, speed and fades (without the slipped clip's
    /// source range), the dissolves, the markers, the track flags, the sequence length and the playhead.</summary>
    private static string Invariants(TimelineFixture f, Clip slipped)
    {
        var sb = new StringBuilder();
        foreach (var t in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks))
        {
            sb.Append($"{t.Id}:order={t.Order}:m{t.IsMuted}h{t.IsHidden}l{t.IsLocked}|");
            foreach (var c in t.Clips)
            {
                var s = ClipState.Capture(c);
                sb.Append($"{c.Id}:{s.Start.Ticks},{s.Duration.Ticks},{s.Speed},{c.FadeIn.Ticks}/{c.FadeOut.Ticks}");
                if (c != slipped) sb.Append($",{s.SourceIn.Ticks},{s.SourceOut.Ticks}");
                sb.Append(';');
            }
            foreach (var tr in t.Transitions) sb.Append($"T{tr.Id}:{tr.LeftClipId}|{tr.RightClipId}:{tr.Duration.Ticks};");
            sb.Append('\n');
        }
        foreach (var m in f.Project.Timeline.Markers) sb.Append($"marker@{m.Position.Ticks};");
        sb.Append($"duration={f.Project.Timeline.Duration().Ticks};playhead={f.Project.Timeline.PlayheadPosition.Ticks}");
        return sb.ToString();
    }

    /// <summary>D022's amount for a slip of <paramref name="k"/> frames, computed here independently of the service.</summary>
    private static MediaTime ExpectedDelta(TimelineFixture f, MediaBackedClip clip, long k)
    {
        if (clip.Speed.IsNormal)
        {
            var s = clip.TimelineStart.ToNearestFrame(f.Rate);
            return MediaTime.FromFrame(s + k, f.Rate) - MediaTime.FromFrame(s, f.Rate);
        }
        var length = SpeedTiming.SourceLength(Math.Abs(k), clip.Speed, f.Rate);
        return k >= 0 ? length : MediaTime.Zero - length;
    }

    /// <summary>A 20 s video at <paramref name="rate"/> split at 100 and 300 with A and C removed: B on V1 at frame 100 with
    /// 100 frames of source before it and 200 after (at <paramref name="speedSteps"/>/20), a clip after a gap, an audio
    /// clip on A1, a marker, the playhead at 77.</summary>
    private static (TimelineFixture F, MediaBackedClip B) Middle(FrameRate rate, int speedSteps = 20)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, rate);
        Ok(f.Service.AddClip(video.Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.Split(F(f, 300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).ToList();
        Ok(f.Service.DeleteClips(TimelineFixture.Ids(clips[0], clips[2])));
        var b = (MediaBackedClip)clips[1];
        if (speedSteps != 20) Ok(f.Service.SetClipSpeed(b.Id, ClipSpeed.FromSteps(speedSteps)));
        Ok(f.Service.AddClip(video.Id, f.V1.Id, b.TimelineEnd + F(f, 20)));
        Ok(f.Service.AddClip(f.Audio(30).Id, f.A1.Id, F(f, 50)));
        Ok(f.Service.AddMarker(F(f, 150)));
        f.Project.Timeline.PlayheadPosition = F(f, 77);
        return (f, b);
    }

    // --- the mapping at every rate and speed --------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void A_slip_moves_only_the_source_range_by_D022s_amount_at_every_rate_and_speed(int rateIndex, int speedSteps)
    {
        var (f, b) = Middle(Rates[rateIndex], speedSteps);
        foreach (var k in new long[] { 1, 7, -1, -13 })
        {
            var invariants = Invariants(f, b);
            var (sourceIn, sourceOut) = (b.SourceIn, b.SourceOut);
            var expected = ExpectedDelta(f, b, k);

            var result = f.Service.SlipClip(b.Id, k);

            Ok(result);
            Assert.Null(result.Message);
            Assert.Equal(invariants, Invariants(f, b));                                      // the timeline doesn't change
            Assert.Equal(sourceIn + expected, b.SourceIn);
            Assert.Equal(sourceOut + expected, b.SourceOut);                                  // the width stays
            Assert.Equal("Slip Clip", Top(f));
            f.AssertValid();                                                                  // D022's invariant

            f.UndoRedo.Undo();
            Assert.Equal((sourceIn, sourceOut), (b.SourceIn, b.SourceOut));
            f.UndoRedo.Redo();
            Assert.Equal(sourceIn + expected, b.SourceIn);
            f.UndoRedo.Undo();
        }
    }

    [Theory]
    [InlineData(5, 4, 400_000)]        // 0.25×: four timeline frames are one source frame (0.04 s) at 25 fps
    [InlineData(10, 2, 400_000)]       // 0.5×: two timeline frames are one source frame
    [InlineData(20, 1, 400_000)]       // 1×: one is one
    [InlineData(40, 1, 800_000)]       // 2×: one timeline frame is two source frames
    [InlineData(80, 1, 1_600_000)]     // 4×: one timeline frame is four source frames
    [InlineData(80, -3, -4_800_000)]
    public void A_timeline_frame_of_slip_is_the_speed_times_a_source_frame(int speedSteps, long frames, long ticks)
    {
        var (f, b) = Middle(FrameRate.Fps25, speedSteps);
        var sourceIn = b.SourceIn;

        Ok(f.Service.SlipClip(b.Id, frames));

        Assert.Equal(new MediaTime(ticks), b.SourceIn - sourceIn);
    }

    // --- source limits ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(80)]
    public void A_slip_past_the_source_stops_at_its_start_or_end_with_a_message(int speedSteps)
    {
        var (f, b) = Middle(FrameRate.Ntsc30, speedSteps);
        var invariants = Invariants(f, b);
        var duration = f.Project.MediaAssets[0].Metadata!.Duration;

        var start = f.Service.SlipClip(b.Id, -100_000);
        Ok(start);
        Assert.Equal("The slip stopped at the start of the source.", start.Message);
        Assert.True(b.SourceIn >= MediaTime.Zero);
        Assert.Equal(invariants, Invariants(f, b));
        f.AssertValid();
        var top = f.UndoRedo.CurrentPosition;
        var further = f.Service.SlipClip(b.Id, -1);                                          // nothing left before it
        Assert.False(further.Success);
        Assert.Equal("The slip stopped at the start of the source.", further.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        f.UndoRedo.Undo();

        var end = f.Service.SlipClip(b.Id, 100_000);
        Ok(end);
        Assert.Equal("The slip stopped at the end of the source.", end.Message);
        Assert.True(b.SourceOut <= duration);
        Assert.Equal(invariants, Invariants(f, b));
        f.AssertValid();
        Assert.Equal("The slip stopped at the end of the source.", f.Service.SlipClip(b.Id, 1).Message);
    }

    // --- dissolves -----------------------------------------------------------------------------------------------------

    /// <summary>The 20 s video at 25 fps split at 100 and 300 (A, B, C sharing the source) with a 20-frame dissolve on A | B.</summary>
    private static (TimelineFixture F, MediaBackedClip A, MediaBackedClip B, Transition D) Dissolved()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        Ok(f.Service.Split(F(f, 300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).Cast<MediaBackedClip>().ToList();
        Ok(f.Service.AddTransition(clips[0].Id, clips[1].Id, F(f, 20)));
        return (f, clips[0], clips[1], f.V1.Transitions.Single());
    }

    [Fact]
    public void A_dissolve_keeps_the_source_it_needs_the_slip_stops_there_and_the_dissolve_stays()
    {
        var (f, a, b, dissolve) = Dissolved();

        var bEarlier = f.Service.SlipClip(b.Id, -1000);                                     // B needs 10 frames before it
        Ok(bEarlier);
        Assert.Equal("The slip stopped where a dissolve needs the clip's source.", bEarlier.Message);
        Assert.Equal(F(f, 10), b.SourceIn);
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
        f.UndoRedo.Undo();

        var aLater = f.Service.SlipClip(a.Id, 1000);                                         // A needs 10 frames after it
        Ok(aLater);
        Assert.Equal("The slip stopped where a dissolve needs the clip's source.", aLater.Message);
        Assert.Equal(F(f, 490), a.SourceOut);
        Assert.Contains(dissolve, f.V1.Transitions);
        f.AssertValid();
        f.UndoRedo.Undo();

        var top = f.UndoRedo.CurrentPosition;
        var aEarlier = f.Service.SlipClip(a.Id, -1000);                                     // the other way: the source start
        Assert.False(aEarlier.Success);                                                      // it was already at 0: no step
        Assert.Equal("The slip stopped at the start of the source.", aEarlier.Message);
        Assert.Equal(MediaTime.Zero, a.SourceIn);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void A_slip_with_no_room_changes_nothing_and_leaves_no_step()
    {
        var (f, a, _, _) = Dissolved();                                                      // A starts at source 0
        var top = f.UndoRedo.CurrentPosition;
        var result = f.Service.SlipClip(a.Id, -5);
        Assert.False(result.Success);
        Assert.Equal("The slip stopped at the start of the source.", result.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(MediaTime.Zero, a.SourceIn);
    }

    // --- refusals -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Images_text_an_unknown_length_and_a_locked_track_are_refused()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTextClip(MediaTime.Zero));
        var text = f.V1.Clips[0];
        Ok(f.Service.AddClip(f.Image().Id, f.V1.Id, F(f, 500)));
        var image = f.V1.Clips.Single(c => c is ImageClip);
        var unknown = f.AddAsset("noLength.mp4", MediaKind.Video, new MediaMetadata { Duration = MediaTime.FromSeconds(4), FrameRate = FrameRate.Fps30 });
        Ok(f.Service.AddClip(unknown.Id, f.V1.Id, F(f, 900)));
        var video = f.V1.Clips.Single(c => c is VideoClip);
        unknown.Metadata = null;                                                             // the length is gone (e.g. an old file)
        var top = f.UndoRedo.CurrentPosition;

        Assert.Equal("Only video and audio clips can be slipped.", f.Service.SlipClip(text.Id, 3).Message);
        Assert.Equal("Only video and audio clips can be slipped.", f.Service.SlipClip(image.Id, 3).Message);
        Assert.Null(f.Service.PreviewSlip(image.Id, 3));
        Assert.Equal("The clip's media has no known length, so it can't be slipped.", f.Service.SlipClip(video.Id, 3).Message);

        var (g, b) = Middle(FrameRate.Fps25);
        Ok(g.Service.SetTrackLocked(g.V1.Id, true));
        var sourceIn = b.SourceIn;
        Assert.Equal("Track V1 is locked.", g.Service.SlipClip(b.Id, 3).Message);
        Assert.Null(g.Service.PreviewSlip(b.Id, 3));
        Assert.Equal(sourceIn, b.SourceIn);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Hidden_and_muted_tracks_and_audio_clips_slip_and_keep_their_flags()
    {
        var (f, b) = Middle(FrameRate.Fps25);
        Ok(f.Service.SetTrackHidden(f.V1.Id, true));
        Ok(f.Service.SetTrackMuted(f.A1.Id, true));
        var invariants = Invariants(f, b);
        Ok(f.Service.SlipClip(b.Id, 5));
        Assert.Equal(invariants, Invariants(f, b));

        var audio = (MediaBackedClip)f.A1.Clips[0];
        Ok(f.Service.TrimClip(audio.Id, ClipEdge.Start, F(f, 80)));                          // 30 frames of source before it
        var audioIn = audio.SourceIn;
        Ok(f.Service.SlipClip(audio.Id, -10));
        Assert.Equal(audioIn - (F(f, 90) - F(f, 80)), audio.SourceIn);
        Assert.Equal((F(f, 80), true), (audio.TimelineStart, f.A1.IsMuted));
        f.AssertValid();
    }

    // --- preview, notifications, undo / dirty -----------------------------------------------------------------------------

    [Fact]
    public void The_preview_applies_nothing_notifies_nothing_and_shows_what_the_release_does()
    {
        var (f, b) = Middle(FrameRate.Ntsc24, 10);
        var state = ClipState.Capture(b);
        var top = f.UndoRedo.CurrentPosition;
        var changes = f.TimelineChangedCount;
        var dirty = f.Project.IsDirty;

        var preview = f.Service.PreviewSlip(b.Id, -9);
        var clamped = f.Service.PreviewSlip(b.Id, -100_000);

        Assert.NotNull(preview);
        Assert.Equal(state, ClipState.Capture(b));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);                                       // the Preview isn't rebuilt
        Assert.Equal(dirty, f.Project.IsDirty);
        Assert.Equal((-9L, (string?)null), (preview!.Frames, preview.Note));
        Assert.Equal("The slip stopped at the start of the source.", clamped!.Note);

        Ok(f.Service.SlipClip(b.Id, -9));
        Assert.Equal(changes + 1, f.TimelineChangedCount);                                   // one refresh, on the commit
        Assert.Equal((preview.SourceIn, preview.SourceOut), (b.SourceIn, b.SourceOut));
    }

    [Fact]
    public async Task One_slip_is_one_undo_step_with_the_save_point_and_zero_frames_is_no_step()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var (f, b) = Middle(FrameRate.Fps30);
            await f.Projects.SaveAsAsync(Path.Combine(root, "Project"));
            var saved = ClipState.Capture(b);

            Ok(f.Service.SlipClip(b.Id, 12));
            Assert.True(f.Project.IsDirty);
            f.UndoRedo.Undo();
            Assert.Equal(saved, ClipState.Capture(b));
            Assert.False(f.Project.IsDirty);

            var top = f.UndoRedo.CurrentPosition;
            var zero = f.Service.SlipClip(b.Id, 0);
            Assert.True(zero is { Success: true, NoChange: true });
            Assert.Same(top, f.UndoRedo.CurrentPosition);
            Assert.False(f.Project.IsDirty);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
