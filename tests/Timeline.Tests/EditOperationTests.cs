using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

public class EditOperationTests
{
    private static TimelineFixture WithVideo(FrameRate rate, double seconds, out VideoClip clip, out MediaAsset asset)
    {
        var f = new TimelineFixture();
        asset = f.Video(seconds, rate);
        Assert.True(f.Service.AddClip(asset.Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        return f;
    }

    private static void AssertUndoRedoExact(TimelineFixture f, string before, Func<bool> operation)
    {
        Assert.True(operation());
        f.AssertValid();
        var after = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.AssertValid();

        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    // --- Move --------------------------------------------------------------------

    [Fact]
    public void Move_ByFrames_UndoRedoExact()
    {
        var f = WithVideo(FrameRate.Ntsc30, 10, out var clip, out _);
        var before = f.Snapshot();

        AssertUndoRedoExact(f, before, () => f.Service.MoveClips(TimelineFixture.Ids(clip), 37).Success);
        Assert.Equal(MediaTime.FromFrame(37, f.Rate), clip.TimelineStart);
    }

    [Fact]
    public void Move_IntoOverlap_OrBeforeZero_IsRejected_WithoutChanges()
    {
        var f = new TimelineFixture();
        var img = f.Image();
        f.Service.AddClip(img.Id);
        f.Service.AddClip(img.Id);
        var before = f.Snapshot();

        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(f.V1.Clips[0]), 10).Success);
        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(f.V1.Clips[1]), -10_000).Success);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(2, CountUndo(f));
    }

    [Fact]
    public void Move_ToAnotherCompatibleTrack_AndRejectIncompatible()
    {
        var f = new TimelineFixture();
        f.Service.AddTrack(TrackType.Video);
        var v2 = f.Project.Timeline.VideoTracks[1];
        f.Service.AddClip(f.Image().Id);
        var clip = f.V1.Clips[0];

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 0, v2.Id).Success);
        Assert.Same(clip, Assert.Single(v2.Clips));
        Assert.Empty(f.V1.Clips);

        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(clip), 0, f.A1.Id).Success);
        f.AssertValid();
    }

    [Fact]
    public void Move_MultiTrackSelection_ToTargetTrack_IsRejected()
    {
        var f = new TimelineFixture();
        f.Service.AddClip(f.Image().Id);
        f.Service.AddClip(f.Audio(5).Id);

        var result = f.Service.MoveClips(TimelineFixture.Ids(f.V1.Clips[0], f.A1.Clips[0]), 5, f.V1.Id);
        Assert.False(result.Success);

        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(f.V1.Clips[0], f.A1.Clips[0]), 5).Success);
        f.AssertValid();
    }

    [Fact]
    public void Move_SplitRightHalf_AtSourceEnd_NeverPassesSourceEnd()
    {
        // At 29.97 the tick length of the same frame count varies by one tick between
        // positions; moving the right half (which ends at the source end) must keep
        // SourceOut ≤ source duration at every position.
        var f = WithVideo(FrameRate.Ntsc30, 10, out var clip, out var asset);
        Assert.True(f.Service.Split(MediaTime.FromFrame(100, f.Rate)).Success);
        var right = f.V1.Clips[1];

        for (var i = 0; i < 12; i++)
        {
            Assert.True(f.Service.MoveClips(TimelineFixture.Ids(right), 1).Success);
            Assert.True(((MediaBackedClip)right).SourceOut <= asset.Metadata!.Duration);
            f.AssertValid();
        }
    }

    // --- Trim --------------------------------------------------------------------

    [Fact]
    public void TrimEnd_ClampsToSourceEnd_Neighbour_AndOneFrameMinimum()
    {
        var f = WithVideo(FrameRate.Fps25, 4, out var clip, out var asset);
        var full = clip.TimelineEnd;

        f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(60)); // beyond source
        Assert.Equal(full, clip.TimelineEnd);

        f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.Zero); // below minimum
        Assert.Equal(MediaTime.FromFrame(1, f.Rate), clip.TimelineEnd);

        var img = f.Image();
        f.Service.AddClip(img.Id, f.V1.Id, MediaTime.FromFrame(50, f.Rate));
        f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(3));
        Assert.Equal(MediaTime.FromFrame(50, f.Rate), clip.TimelineEnd); // stops at neighbour
        f.AssertValid();
    }

    [Fact]
    public void TrimStart_MovesSourceIn_KeepsSourceOut_AndClampsAtSourceStart()
    {
        var f = WithVideo(FrameRate.Ntsc30, 10, out var clip, out _);
        f.Service.MoveClips(TimelineFixture.Ids(clip), 100);
        var sourceOut = clip.SourceOut;
        var before = f.Snapshot();

        AssertUndoRedoExact(f, before, () => f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromFrame(130, f.Rate)).Success);
        Assert.Equal(MediaTime.FromFrame(130, f.Rate) - MediaTime.FromFrame(100, f.Rate), clip.SourceIn);
        Assert.Equal(sourceOut, clip.SourceOut);

        f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.Zero); // can't reveal before source start
        Assert.Equal(MediaTime.Zero, clip.SourceIn);
        Assert.Equal(MediaTime.FromFrame(100, f.Rate), clip.TimelineStart);
        f.AssertValid();
    }

    [Fact]
    public void TrimImage_CanExtendFreely_SourceInStaysZero()
    {
        var f = new TimelineFixture();
        f.Service.AddClip(f.Image().Id, f.V1.Id, MediaTime.FromSeconds(2));
        var clip = (ImageClip)f.V1.Clips[0];

        f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(60));
        f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.Zero);

        Assert.Equal(MediaTime.Zero, clip.TimelineStart);
        Assert.Equal(MediaTime.FromSeconds(60), clip.TimelineEnd);
        Assert.Equal(MediaTime.Zero, clip.SourceIn);
        f.AssertValid();
    }

    [Fact]
    public void Trim_ToSamePosition_IsNoChange_AndCreatesNoUndoStep()
    {
        var f = WithVideo(FrameRate.Fps24, 5, out var clip, out _);
        var result = f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineEnd);
        Assert.True(result.NoChange);
        Assert.Equal(1, CountUndo(f));
    }

    // --- Split -------------------------------------------------------------------

    [Theory]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30, 1)]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void Split_IsExact_AndUndoRedoKeepsIds(int num, int den)
    {
        var f = WithVideo(new FrameRate(num, den), 10, out var clip, out _);
        f.Service.MoveClips(TimelineFixture.Ids(clip), 7);
        var original = ClipState.Capture(clip);
        var before = f.Snapshot();

        AssertUndoRedoExact(f, before, () => f.Service.Split(MediaTime.FromFrame(107, f.Rate)).Success);

        var left = (VideoClip)f.V1.Clips[0];
        var right = (VideoClip)f.V1.Clips[1];
        Assert.Same(clip, left);
        Assert.Equal(original.Duration, left.Duration + right.Duration);
        Assert.Equal(left.TimelineEnd, right.TimelineStart);
        Assert.Equal(left.SourceOut, right.SourceIn);
        Assert.Equal(original.SourceOut, right.SourceOut);
        Assert.Equal(original.SourceIn, left.SourceIn);
    }

    [Fact]
    public void Split_CopiesClipProperties()
    {
        var f = WithVideo(FrameRate.Fps30, 5, out var clip, out _);
        clip.Volume = 0.4;
        clip.Opacity = 0.7;
        f.Service.Split(MediaTime.FromSeconds(2));

        var right = (VideoClip)f.V1.Clips[1];
        Assert.NotEqual(clip.Id, right.Id);
        Assert.Equal(0.4, right.Volume);
        Assert.Equal(0.7, right.Opacity);
        Assert.Equal(clip.MediaAssetId, right.MediaAssetId);
    }

    [Fact]
    public void Split_AtEdge_OrOutsideSelection_IsRejected()
    {
        var f = WithVideo(FrameRate.Fps30, 5, out var clip, out _);
        var before = f.Snapshot();

        Assert.False(f.Service.Split(MediaTime.Zero).Success);
        Assert.False(f.Service.Split(clip.TimelineEnd).Success);
        Assert.False(f.Service.Split(MediaTime.FromSeconds(60)).Success);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Split_WithoutSelection_SplitsAllClipsUnderPlayhead_OnUnlockedTracks()
    {
        var f = new TimelineFixture();
        f.Service.AddClip(f.Image().Id);
        f.Service.AddClip(f.Audio(10).Id);
        f.Service.AddTrack(TrackType.Video);
        var v2 = f.Project.Timeline.VideoTracks[1];
        f.Service.AddClip(f.Image("b.png").Id, v2.Id);
        v2.IsLocked = true;

        Assert.True(f.Service.Split(MediaTime.FromSeconds(1)).Success);

        Assert.Equal(2, f.V1.Clips.Count);
        Assert.Equal(2, f.A1.Clips.Count);
        Assert.Single(v2.Clips);
        f.AssertValid();
    }

    // --- Delete ------------------------------------------------------------------

    [Fact]
    public void Delete_UndoRestoresSameObjects()
    {
        var f = new TimelineFixture();
        var img = f.Image();
        f.Service.AddClip(img.Id);
        f.Service.AddClip(img.Id);
        var first = f.V1.Clips[0];
        var before = f.Snapshot();

        AssertUndoRedoExact(f, before, () => f.Service.DeleteClips(TimelineFixture.Ids(first)).Success);
        f.UndoRedo.Undo();
        Assert.Same(first, f.V1.Clips[0]);
    }

    // --- Locked / speed ----------------------------------------------------------

    [Fact]
    public void LockedTrack_RejectsEdits()
    {
        var f = WithVideo(FrameRate.Fps30, 5, out var clip, out _);
        f.V1.IsLocked = true;
        var before = f.Snapshot();

        Assert.False(f.Service.MoveClips(TimelineFixture.Ids(clip), 5).Success);
        Assert.False(f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromSeconds(1)).Success);
        Assert.False(f.Service.DeleteClips(TimelineFixture.Ids(clip)).Success);
        Assert.False(f.Service.AddClip(f.Image().Id).Success);
        Assert.Equal(before, f.Snapshot());
    }

    // --- Randomized --------------------------------------------------------------

    [Theory]
    [InlineData(30000, 1001, 1)]
    [InlineData(24, 1, 2)]
    [InlineData(60000, 1001, 3)]
    public void RandomOperationSequence_KeepsInvariants_AndFullUndoRestoresStart(int num, int den, int seed)
    {
        var f = new TimelineFixture();
        var video = f.Video(30, new FrameRate(num, den));
        var audio = f.Audio(20);
        var image = f.Image();
        var start = f.Snapshot();
        f.Service.AddTrack(TrackType.Video);
        var random = new Random(seed);

        for (var step = 0; step < 400; step++)
        {
            var clips = f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).ToList();
            var pick = clips.Count > 0 ? clips[random.Next(clips.Count)] : null;
            var at = MediaTime.FromFrame(random.Next(0, 2000), f.Rate);

            switch (random.Next(7))
            {
                case 0: f.Service.AddClip(new[] { video, audio, image }[random.Next(3)].Id); break;
                case 1: if (pick is not null) f.Service.MoveClips(TimelineFixture.Ids(pick), random.Next(-200, 200)); break;
                case 2: if (pick is not null) f.Service.TrimClip(pick.Id, random.Next(2) == 0 ? ClipEdge.Start : ClipEdge.End, at); break;
                case 3: f.Service.Split(at, pick is not null && random.Next(2) == 0 ? TimelineFixture.Ids(pick) : null); break;
                case 4: if (pick is not null && random.Next(3) == 0) f.Service.DeleteClips(TimelineFixture.Ids(pick)); break;
                case 5: if (f.UndoRedo.CanUndo) f.UndoRedo.Undo(); break;
                case 6: if (f.UndoRedo.CanRedo) f.UndoRedo.Redo(); break;
            }

            f.AssertValid();
        }

        while (f.UndoRedo.CanUndo) f.UndoRedo.Undo();
        Assert.Equal(start, f.Snapshot());
        Assert.False(f.Settings.IsFrameRateLocked);
    }

    private static int CountUndo(TimelineFixture f)
    {
        var n = 0;
        while (f.UndoRedo.CanUndo) { f.UndoRedo.Undo(); n++; }
        for (var i = 0; i < n; i++) f.UndoRedo.Redo();
        return n;
    }
}
