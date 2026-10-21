using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// The Phase 28 manual run's D7 (D044 §3): the trim and the slip keep the source handles a transition needs only for the
/// types that need them — Dissolve and Push on a video track, the crossfade on an audio track (D025 §4, D032) — and not for
/// Dip to black, Flash and Zoom, which draw no frame beyond their clips. The zone's frames inside each clip are kept for
/// every type (D025 §5). Undo / Redo restore every edit.
/// </summary>
public class TransitionHandleReservationTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static long Frame(MediaTime t) => t.ToNearestFrame(Rate);
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    /// <summary>A 20 s video at 25 fps (500 frames) split at 100 and 300: A [0, 100), B [100, 300), C [300, 500), one source,
    /// with a transition of <paramref name="type"/> on A | B.</summary>
    private static (TimelineFixture F, MediaBackedClip A, MediaBackedClip B, Transition T) Cut(string type)
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        Ok(f.Service.AddClip(f.Video(20, Rate).Id));
        Ok(f.Service.Split(F(100)));
        Ok(f.Service.Split(F(300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).Cast<MediaBackedClip>().ToList();
        var result = f.Service.AddTransition(clips[0].Id, clips[1].Id, type);
        Ok(result);
        return (f, clips[0], clips[1], f.V1.Transitions.Single(t => t.Id == result.TransitionId));
    }

    private static (long BeforeCut, long AfterCut) Zone(Transition t) => TransitionRules.Zone(TransitionRules.Frames(t.Duration, Rate));

    /// <summary>Runs the edit, checks the expectation, then Undo back to the state before and Redo to the state after.</summary>
    private static void UndoRedo(TimelineFixture f, Func<TimelineEditResult> edit, Action check)
    {
        var before = f.Snapshot();
        Ok(edit());
        check();
        f.AssertValid();
        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
        f.UndoRedo.Undo();
    }

    [Theory]
    [InlineData("dipToBlack")]
    [InlineData("flash")]
    [InlineData("zoom")]
    public void A_slip_reaches_the_sources_ends_beside_a_transition_without_handles(string type)
    {
        var (f, a, b, t) = Cut(type);

        UndoRedo(f, () => f.Service.SlipClip(b.Id, -1000), () =>
        {
            Assert.Equal(0, Frame(b.SourceIn));                                           // nothing kept before B
            Assert.Equal(type, Assert.Single(f.V1.Transitions).TransitionTypeId);
        });
        Assert.Equal("The slip stopped at the start of the source.", f.Service.SlipClip(b.Id, -1000).Message);
        f.UndoRedo.Undo();

        UndoRedo(f, () => f.Service.SlipClip(a.Id, 1000), () => Assert.Equal(500, Frame(a.SourceOut)));   // nothing after A
        Assert.Contains(t, f.V1.Transitions);
    }

    [Theory]
    [InlineData("crossDissolve")]
    [InlineData("push")]
    public void A_slip_keeps_the_handles_of_a_dissolve_or_a_push(string type)
    {
        var (f, a, b, t) = Cut(type);
        var (beforeCut, afterCut) = Zone(t);

        UndoRedo(f, () => f.Service.SlipClip(b.Id, -1000), () => Assert.Equal(beforeCut, Frame(b.SourceIn)));
        UndoRedo(f, () => f.Service.SlipClip(a.Id, 1000), () => Assert.Equal(500 - afterCut, Frame(a.SourceOut)));
    }

    [Theory]
    [InlineData("dipToBlack", false)]
    [InlineData("flash", false)]
    [InlineData("zoom", false)]
    [InlineData("crossDissolve", true)]
    [InlineData("push", true)]
    public void A_ripple_trim_outward_on_the_cut_keeps_a_handle_only_for_the_types_that_need_one(string type, bool handles)
    {
        var (f, a, b, t) = Cut(type);
        var (beforeCut, afterCut) = Zone(t);

        UndoRedo(f, () => f.Service.RippleTrimClip(a.Id, ClipEdge.End, F(1000)), () =>
        {
            Assert.Equal(handles ? 500 - afterCut : 500, Frame(a.TimelineEnd));            // A's source ends at 500
            Assert.Equal(Frame(a.TimelineEnd), Frame(b.TimelineStart));                     // B follows: the cut stays
            Assert.Contains(t, f.V1.Transitions);
        });
        UndoRedo(f, () => f.Service.RippleTrimClip(b.Id, ClipEdge.Start, F(-1000)), () =>
            Assert.Equal(handles ? beforeCut : 0, Frame(b.SourceIn)));                     // B's source starts at 0
    }

    [Theory]
    [InlineData("dipToBlack")]
    [InlineData("flash")]
    [InlineData("zoom")]
    [InlineData("crossDissolve")]
    [InlineData("push")]
    public void A_trim_inward_keeps_the_zones_frames_inside_the_clip_for_every_type(string type)
    {
        var (f, a, b, t) = Cut(type);
        var (beforeCut, afterCut) = Zone(t);

        // A plain trim of the far edge: A keeps its part of the zone.
        UndoRedo(f, () => f.Service.TrimClip(a.Id, ClipEdge.Start, F(99)), () =>
            Assert.Equal(100 - beforeCut, Frame(a.TimelineStart)));
        // A ripple trim of the cut edge inward: B keeps its part of the zone.
        var result = f.Service.RippleTrimClip(b.Id, ClipEdge.End, F(101));
        Ok(result);
        Assert.Equal(afterCut, Frame(b.TimelineEnd) - Frame(b.TimelineStart));
        f.AssertValid();
        f.UndoRedo.Undo();
    }

    [Fact]
    public void Beside_a_dissolve_and_a_flash_only_the_dissolve_keeps_a_handle_and_the_note_names_it()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        Ok(f.Service.AddClip(f.Video(20, Rate).Id));
        Ok(f.Service.Split(F(100)));
        Ok(f.Service.Split(F(300)));
        var clips = f.V1.Clips.OrderBy(c => c.TimelineStart).Cast<MediaBackedClip>().ToList();
        var b = clips[1];
        Ok(f.Service.AddTransition(clips[0].Id, b.Id, TransitionRules.Flash));             // at B's start: no handle
        var dissolve = f.Service.AddTransition(b.Id, clips[2].Id, TransitionRules.CrossDissolve);   // at B's end: a handle
        Ok(dissolve);
        var afterCut = Zone(f.V1.Transitions.Single(t => t.Id == dissolve.TransitionId)).AfterCut;

        UndoRedo(f, () => f.Service.SlipClip(b.Id, -1000), () => Assert.Equal(0, Frame(b.SourceIn)));
        var later = f.Service.SlipClip(b.Id, 1000);
        Ok(later);
        Assert.Equal(500 - afterCut, Frame(b.SourceOut));
        Assert.Equal("The slip stopped where a dissolve needs the clip's source.", later.Message);
        Assert.Equal(2, f.V1.Transitions.Count);
        f.UndoRedo.Undo();

        var trim = f.Service.RippleTrimClip(b.Id, ClipEdge.End, F(1000));
        Ok(trim);
        Assert.Equal("The trim stopped where a dissolve needs the clip's frames.", trim.Message);
    }

    [Fact]
    public void An_audio_crossfade_keeps_its_handles()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        var a1 = f.Project.Timeline.AudioTracks[0];
        Ok(f.Service.AddClip(f.Audio(20).Id, a1.Id, F(0)));
        Ok(f.Service.Split(F(100), new[] { a1.Clips[0].Id }));
        var (a, b) = (a1.Clips[0], (MediaBackedClip)a1.Clips[1]);
        Ok(f.Service.AddTransition(a.Id, b.Id, F(20)));                                      // 10 frames before the cut
        var beforeCut = Zone(Assert.Single(a1.Transitions)).BeforeCut;

        UndoRedo(f, () => f.Service.SlipClip(b.Id, -1000), () => Assert.Equal(beforeCut, Frame(b.SourceIn)));
    }
}
