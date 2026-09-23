using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

public class AddClipAndFrameRateTests
{
    [Fact]
    public void NewProject_HasV1AndA1_AndProvisionalThirtyFps()
    {
        var f = new TimelineFixture();
        Assert.Equal("V1", f.V1.Name);
        Assert.Equal("A1", f.A1.Name);
        Assert.Equal(FrameRate.Fps30, f.Rate);
        Assert.False(f.Settings.IsFrameRateLocked);

        f.Projects.CreateNew("Another");
        Assert.Single(f.Project.Timeline.VideoTracks);
        Assert.Single(f.Project.Timeline.AudioTracks);
    }

    [Fact]
    public void Image_IsAddedImmediately_FiveSeconds_WithoutLockingFrameRate()
    {
        var f = new TimelineFixture();
        var result = f.Service.AddClip(f.Image().Id);

        Assert.True(result.Success, result.Message);
        var clip = Assert.IsType<ImageClip>(Assert.Single(f.V1.Clips));
        Assert.Equal(MediaTime.FromSeconds(5), clip.Duration);
        Assert.Equal(MediaTime.Zero, clip.SourceIn);
        Assert.False(f.Settings.IsFrameRateLocked);
        f.AssertValid();
    }

    [Fact]
    public void Audio_GoesToA1_WithWholeFramesFittingTheSource()
    {
        var f = new TimelineFixture();
        var audio = f.Audio(10.01);
        Assert.True(f.Service.AddClip(audio.Id).Success);

        var clip = Assert.IsType<AudioClip>(Assert.Single(f.A1.Clips));
        Assert.Equal(300, clip.Duration.ToFrameFloor(f.Rate)); // 10.01 s → 300 whole frames at 30
        Assert.True(clip.SourceOut <= audio.Metadata!.Duration);
        Assert.False(f.Settings.IsFrameRateLocked);
        f.AssertValid();
    }

    [Fact]
    public void AddWithoutPosition_AppendsAfterLastClip()
    {
        var f = new TimelineFixture();
        var img = f.Image();
        f.Service.AddClip(img.Id);
        f.Service.AddClip(img.Id);

        Assert.Equal(2, f.V1.Clips.Count);
        Assert.Equal(f.V1.Clips[0].TimelineEnd, f.V1.Clips[1].TimelineStart);
        f.AssertValid();
    }

    [Theory]
    [InlineData(MediaAnalysisStatus.Pending)]
    [InlineData(MediaAnalysisStatus.Analyzing)]
    [InlineData(MediaAnalysisStatus.Failed)]
    public void VideoAndAudio_AreBlocked_UntilAnalysisSucceeds(MediaAnalysisStatus status)
    {
        var f = new TimelineFixture();
        var video = f.AddAsset("v.mp4", MediaKind.Video, null, status);
        var audio = f.AddAsset("a.mp3", MediaKind.Audio, null, status);

        Assert.False(f.Service.AddClip(video.Id).Success);
        Assert.False(f.Service.AddClip(audio.Id).Success);
        Assert.NotNull(f.Service.GetAddBlockReason(video));
        Assert.Empty(f.V1.Clips);
        Assert.Empty(f.A1.Clips);
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Fact]
    public void WrongTrackType_AndOverlappingExplicitStart_AreRejected_WithoutChanges()
    {
        var f = new TimelineFixture();
        var img = f.Image();
        f.Service.AddClip(img.Id);
        var before = f.Snapshot();

        Assert.False(f.Service.AddClip(img.Id, f.A1.Id).Success);
        Assert.False(f.Service.AddClip(img.Id, f.V1.Id, MediaTime.FromSeconds(2)).Success);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void ExplicitStart_IsSnappedToFrameGrid()
    {
        var f = new TimelineFixture();
        var result = f.Service.AddClip(f.Image().Id, f.V1.Id, new MediaTime(MediaTime.FromFrame(10, FrameRate.Fps30).Ticks + 7));

        Assert.True(result.Success);
        Assert.Equal(MediaTime.FromFrame(10, FrameRate.Fps30), f.V1.Clips[0].TimelineStart);
    }

    [Fact]
    public void FirstVideo_LocksProjectFrameRate_FromSource_AndUndoRestoresEverything()
    {
        var f = new TimelineFixture();
        var before = f.Snapshot();

        var result = f.Service.AddClip(f.Video(10, FrameRate.Ntsc30).Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(FrameRate.Ntsc30, f.Rate);
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Contains("29.97", result.Message);
        Assert.DoesNotContain("fallback", result.Message);
        f.AssertValid();
        var after = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.False(f.Settings.IsFrameRateLocked);

        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Theory]
    [InlineData(null)]        // missing / "0/0" (ffprobe) → null metadata rate
    [InlineData("300/1")]     // above 240
    [InlineData("1/2")]       // below 1
    public void UnknownOrOutOfRangeSourceRate_LocksFallbackThirty_AndSaysSo(string? rateText)
    {
        var f = new TimelineFixture();
        FrameRate? rate = rateText is null ? null : (FrameRate.TryParse(rateText, out var r) ? r : null);

        var result = f.Service.AddClip(f.Video(5, rate).Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(FrameRate.Fps30, f.Rate);
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Contains("fallback", result.Message);
        Assert.Contains("not the file's actual frame rate", result.Message);
    }

    [Fact]
    public void AfterLock_OtherVideoRates_DoNotChangeProjectRate_AndDeleteDoesNotUnlock()
    {
        var f = new TimelineFixture();
        f.Service.AddClip(f.Video(5, FrameRate.Fps24).Id);
        var second = f.Service.AddClip(f.Video(5, FrameRate.Ntsc60, "b.mp4").Id);

        Assert.True(second.Success);
        Assert.Null(second.Message);
        Assert.Equal(FrameRate.Fps24, f.Rate);

        f.Service.DeleteClips(f.V1.Clips.Select(c => c.Id).ToList());
        Assert.Empty(f.V1.Clips);
        Assert.True(f.Settings.IsFrameRateLocked);
        Assert.Equal(FrameRate.Fps24, f.Rate);
    }

    [Fact]
    public void FirstVideo_RegridsExistingProvisionalClips_InSameUndoStep()
    {
        var f = new TimelineFixture();
        var img = f.Image();
        var audio = f.Audio(20);
        f.Service.AddClip(img.Id);                 // V1 [0, 5 s)
        f.Service.AddClip(audio.Id);               // A1
        f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, MediaTime.FromFrame(31, FrameRate.Fps30));
        var before = f.Snapshot();
        var undoDepthBefore = CountUndo(f);

        var result = f.Service.AddClip(f.Video(10, FrameRate.Ntsc24).Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(FrameRate.Ntsc24, f.Rate);
        f.AssertValid(); // every clip is on the 23.976 grid now
        Assert.Equal(undoDepthBefore + 1, CountUndo(f));

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void ImpossibleRegrid_RejectsTheVideoAdd_Atomically()
    {
        // Four adjacent 1-frame clips at 30 FPS; at 24 FPS their edges round to frames
        // 0,1,2,2,3 — the third clip collapses with no room on either side.
        var f = new TimelineFixture();
        var img = f.Image();
        for (var i = 0; i < 4; i++)
            f.PlaceImageFrames(f.V1, img, i, i + 1, FrameRate.Fps30);
        var before = f.Snapshot();

        var result = f.Service.AddClip(f.Video(5, FrameRate.Fps24).Id);

        Assert.False(result.Success);
        Assert.Equal(before, f.Snapshot());
        Assert.False(f.Settings.IsFrameRateLocked);
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Fact]
    public void TimelineChanged_AndDirty_OnExecuteUndoRedo()
    {
        var f = new TimelineFixture();
        f.Project.IsDirty = false;

        f.Service.AddClip(f.Image().Id);
        f.UndoRedo.Undo();
        f.UndoRedo.Redo();

        Assert.Equal(3, f.TimelineChangedCount);
        Assert.True(f.Project.IsDirty);
    }

    [Fact]
    public void AddTrack_NamesSequentially_AndIsUndoable()
    {
        var f = new TimelineFixture();
        f.Service.AddTrack(TrackType.Video);
        f.Service.AddTrack(TrackType.Audio);

        Assert.Equal(new[] { "V1", "V2" }, f.Project.Timeline.VideoTracks.Select(t => t.Name));
        Assert.Equal(new[] { "A1", "A2" }, f.Project.Timeline.AudioTracks.Select(t => t.Name));

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Single(f.Project.Timeline.VideoTracks);
    }

    private static int CountUndo(TimelineFixture f)
    {
        // Undo everything and redo it back to measure the stack depth.
        var n = 0;
        while (f.UndoRedo.CanUndo) { f.UndoRedo.Undo(); n++; }
        for (var i = 0; i < n; i++) f.UndoRedo.Redo();
        return n;
    }
}
