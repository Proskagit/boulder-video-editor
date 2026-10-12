using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 17 Step 17.6 (D032 R8, R9): a crossfade is the dissolve's transition on an audio track — the same edits, zone,
/// handle and reconciliation rules (D025 §3–§5), named a crossfade; a fade on its edge is not applied (as PO-8 for a
/// dissolve); audio clips of a video file fade like the video clip's own sound did.
/// </summary>
public class CrossfadeEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    /// <summary>25 fps; A1: A = music [0, 50) from its start, B = music [50, 100) from its frame 60 (handles both sides).</summary>
    private static (TimelineFixture F, AudioClip A, AudioClip B) Cut()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(8, Rate, "picture.mp4").Id));                     // locks 25 fps
        var music = f.Audio(20);
        var a = f.Place<AudioClip>(f.A1, music, 0, F(50).Ticks);
        var b = f.Place<AudioClip>(f.A1, music, F(50).Ticks, F(100).Ticks, F(60).Ticks);
        f.UndoRedo.Clear();
        return (f, a, b);
    }

    [Fact]
    public void A_crossfade_is_added_on_an_audio_cut_as_one_undo_step()
    {
        var (f, a, b) = Cut();
        var before = f.Snapshot();

        var result = f.Service.AddTransition(a.Id, b.Id, F(25));

        Ok(result);
        var crossfade = Assert.Single(f.A1.Transitions);
        Assert.Equal((TransitionRules.Crossfade, F(25), a.Id, b.Id),
            (crossfade.TransitionTypeId, crossfade.Duration, crossfade.LeftClipId, crossfade.RightClipId));
        Assert.Equal(crossfade.Id, result.TransitionId);
        Assert.Equal("Add Crossfade", f.UndoRedo.NextUndo?.Description);
        f.AssertValid();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());

        Assert.Equal(TransitionRules.CrossDissolve, TransitionRules.TypeFor(f.V1.Type));   // video tracks keep dissolves
    }

    [Fact]
    public void The_longest_crossfade_is_limited_by_the_handles_and_the_messages_name_a_crossfade()
    {
        var (f, a, b) = Cut();
        // B's source reaches 60 frames before its start, A's 450 after its end: the clips (50 frames each) limit it.
        Assert.Equal(100, f.Service.MaxTransitionFrames(a.Id, b.Id));

        var tooLong = f.Service.AddTransition(a.Id, b.Id, F(102));
        Assert.False(tooLong.Success);
        Assert.Equal("A crossfade of 102 frames doesn't fit here; the longest that fits is 100 frames.", tooLong.Message);

        var (g, ga, _) = Cut();
        var music = g.Audio(4, "short.wav");
        var c = g.Place<AudioClip>(g.A1, music, F(100).Ticks, F(200).Ticks);
        var noHandle = g.Service.AddTransition(g.A1.Clips[1].Id, c.Id, F(10));          // c's source starts at its start
        Assert.False(noHandle.Success);
        Assert.Equal("There is not enough media beyond the clips for the crossfade.", noHandle.Message);
        Assert.Equal("A crossfade must be at least 2 frames long.", g.Service.AddTransition(ga.Id, g.A1.Clips[1].Id, F(1)).Message);
        Assert.Empty(g.A1.Transitions);
    }

    [Fact]
    public void Edits_keep_or_remove_a_crossfade_by_the_dissolve_rules()
    {
        var (f, a, b) = Cut();
        Ok(f.Service.AddTransition(a.Id, b.Id, F(10)));

        Assert.Equal("Can't split inside a crossfade.", f.Service.Split(F(52), new[] { b.Id }).Message);   // inside the zone
        Ok(f.Service.MoveClips(new[] { a.Id, b.Id }, 10));                                               // together: kept
        Assert.Single(f.A1.Transitions);

        var moved = f.Service.MoveClips(new[] { b.Id }, 5);                                              // apart: removed
        Ok(moved);
        Assert.Equal("A crossfade was removed: its clips no longer meet.", moved.Message);
        Assert.Empty(f.A1.Transitions);
        f.UndoRedo.Undo();
        Assert.Single(f.A1.Transitions);

        Ok(f.Service.SetTransitionDuration(f.A1.Transitions[0].Id, F(20)));
        Assert.Equal("Change Crossfade Duration", f.UndoRedo.NextUndo?.Description);
        Ok(f.Service.RemoveTransition(f.A1.Transitions[0].Id));
        Assert.Equal("Remove Crossfade", f.UndoRedo.NextUndo?.Description);
        f.AssertValid();
    }

    [Fact]
    public void A_far_edge_trim_is_clamped_so_the_crossfade_still_fits()
    {
        var (f, a, b) = Cut();
        Ok(f.Service.AddTransition(a.Id, b.Id, F(20)));            // 10 frames of A before the cut
        Ok(f.Service.TrimClip(a.Id, ClipEdge.Start, F(45)));      // the far edge: clamped so the zone still fits
        Assert.Equal(F(40), a.TimelineStart);
        Assert.Single(f.A1.Transitions);
    }

    [Fact]
    public void A_fade_on_a_crossfade_edge_is_not_applied_and_the_spans_carry_the_zone()
    {
        var (f, a, b) = Cut();
        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(5), F(8)) }));
        Ok(f.Service.SetClipProperties(b.Id, new ClipPropertyChange { Fade = new FadeProperties(F(6), F(9)) }));
        Ok(f.Service.AddTransition(a.Id, b.Id, F(10)));

        var snapshot = PlaybackSnapshotBuilder.Build(f.Project, 1);
        var (spanA, spanB) = (snapshot.AudioSpans.Single(s => s.ClipId == a.Id), snapshot.AudioSpans.Single(s => s.ClipId == b.Id));
        Assert.Equal((5L, 0L), (spanA.FadeInFrames, spanA.FadeOutFrames));
        Assert.Equal((0L, 9L), (spanB.FadeInFrames, spanB.FadeOutFrames));
        Assert.Equal(new CrossfadeZone(F(45), F(55)), spanA.CrossfadeOut);
        Assert.Equal(new CrossfadeZone(F(45), F(55)), spanB.CrossfadeIn);
        Assert.Null(spanA.CrossfadeIn);
        Assert.Null(spanB.CrossfadeOut);
        Assert.Equal((F(0), F(55)), (spanA.PlayStart, spanA.PlayEnd));
        Assert.Equal((F(45), F(100)), (spanB.PlayStart, spanB.PlayEnd));
        Assert.False(snapshot.DiffersOnlyInPresentation(PlaybackSnapshotBuilder.Build(Without(f), 2)));   // a timing change
    }

    private static Core.Entities.Project Without(TimelineFixture f)
    {
        Ok(f.Service.RemoveTransition(f.A1.Transitions[0].Id));
        return f.Project;
    }

    [Fact]
    public void An_audio_clip_of_a_video_file_takes_fades_and_a_crossfade()
    {
        var f = new TimelineFixture();
        var take = f.VideoWithSound(20, Rate);
        Ok(f.Service.AddClip(take.Id));
        Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, F(100)));
        Ok(f.Service.Split(F(50)));
        Ok(f.Service.UnlinkAudio(f.V1.Clips[0].Id));
        Ok(f.Service.UnlinkAudio(f.V1.Clips[1].Id));
        var (a, b) = (f.A1.Clips[0], f.A1.Clips[1]);

        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(10), F(0)) }));
        Ok(f.Service.AddTransition(a.Id, b.Id, F(16)));
        Assert.Equal(TransitionRules.Crossfade, f.A1.Transitions.Single().TransitionTypeId);
        Assert.Empty(f.V1.Transitions);
        f.AssertValid();
    }
}
