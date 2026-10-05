using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 12 Step 12.4 (D027 §4): removing a media asset from the project. An unused asset leaves the media list; a used
/// one takes every clip that uses it — on any track — and their dissolves with it. One Undo step puts the same asset
/// object back at its place (id, path, metadata, analysis state, offline flag) with the clips and dissolves exactly.
/// The file on disk is never touched; a clip on a locked track blocks the removal; rejected removals change nothing
/// and leave no Undo step. The format stays v3.
/// </summary>
public class MediaRemovalTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>The media list (order and every field a removal must restore) plus the timeline snapshot.</summary>
    private static string State(TimelineFixture f) =>
        string.Join("\n", f.Project.MediaAssets.Select(a =>
            $"{a.Id}:{a.FilePath}:{a.Kind}:{a.FileSizeBytes}:{a.AnalysisStatus}:{a.AnalysisError}:{a.IsMissing}:{a.Metadata?.Duration.Ticks}"))
        + "\n" + f.Snapshot();

    private static IReadOnlyCollection<Guid> SnapshotAssets(TimelineFixture f) =>
        PlaybackSnapshotBuilder.Build(f.Project, 1).Assets.Keys.ToList();

    [Fact]
    public void An_unused_asset_leaves_the_project_in_one_undo_step_and_comes_back_at_its_place()
    {
        var f = new TimelineFixture();
        var first = f.Video(10, FrameRate.Fps25, "a.mp4");
        var unused = f.Video(10, FrameRate.Fps25, "b.mp4");
        var last = f.Audio(10, "c.mp3");
        var before = State(f);
        var assetEvents = 0;
        f.Projects.MediaAssetsChanged += (_, _) => assetEvents++;
        var timelineEvents = f.TimelineChangedCount;

        Assert.Equal(0, f.Service.CountClipsUsing(unused.Id));
        Assert.Null(f.Service.GetRemoveMediaBlockReason(unused.Id));
        var result = f.Service.RemoveMedia(unused.Id);

        Ok(result);
        Assert.Empty(result.ClipIds);
        Assert.Equal(new[] { first, last }, f.Project.MediaAssets);
        Assert.Equal("Remove Media", Top(f));
        Assert.True(f.Project.IsDirty);
        Assert.Equal(1, assetEvents);
        Assert.Equal(timelineEvents, f.TimelineChangedCount);            // the timeline did not change
        var after = State(f);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.Same(unused, f.Project.MediaAssets[1]);                  // the same object, in the middle again
        Assert.False(f.Project.IsDirty);                                // the first change of the history undone
        f.UndoRedo.Redo();
        Assert.Equal(after, State(f));
    }

    [Fact]
    public void A_used_asset_takes_every_clip_using_it_and_their_dissolves_with_it_and_undo_restores_them_exactly()
    {
        var f = new TimelineFixture();
        var used = f.Video(20, FrameRate.Fps25, "used.mp4");
        var other = f.Video(20, FrameRate.Fps25, "other.mp4");
        Ok(f.Service.AddClip(used.Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Fade = new FadeProperties(F(f, 10), MediaTime.Zero) }));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks[^1];
        Ok(f.Service.AddClip(used.Id, v2.Id, F(f, 600)));             // a third clip, on another track
        Ok(f.Service.AddClip(other.Id, v2.Id, F(f, 0)));              // a clip of another asset stays
        var keep = v2.Clips.Single(c => ((MediaBackedClip)c).MediaAssetId == other.Id);
        var timelineEvents = f.TimelineChangedCount;
        var before = State(f);

        Assert.Equal(3, f.Service.CountClipsUsing(used.Id));
        var result = f.Service.RemoveMedia(used.Id);

        Ok(result);
        Assert.Equal(3, result.ClipIds.Count);
        Assert.Equal("A dissolve was removed: its clips no longer meet.", result.Message);
        Assert.DoesNotContain(used, f.Project.MediaAssets);
        Assert.Empty(f.V1.Clips);
        Assert.Empty(f.V1.Transitions);
        Assert.Equal(new[] { keep }, v2.Clips);
        Assert.DoesNotContain(used.Id, SnapshotAssets(f));               // nothing of it left for the Preview / export
        Assert.Equal(timelineEvents + 1, f.TimelineChangedCount);
        Assert.Equal("Remove Media", Top(f));
        f.AssertValid();

        f.UndoRedo.Undo();                                             // one step brings everything back
        Assert.Equal(before, State(f));
        Assert.Equal(new[] { a, b }, f.V1.Clips);
        Assert.Single(f.V1.Transitions);
        Assert.Equal(F(f, 10), a.FadeIn);
        Assert.Contains(used.Id, SnapshotAssets(f));
        f.AssertValid();
    }

    [Fact]
    public void The_assets_metadata_and_analysis_state_come_back_with_it()
    {
        var f = new TimelineFixture();
        var failed = f.AddAsset("broken.mp4", MediaKind.Video, null, MediaAnalysisStatus.Failed);
        failed.AnalysisError = "unreadable";
        failed.FileSizeBytes = 1234;
        var metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(3) };
        var analysed = f.AddAsset("fine.wav", MediaKind.Audio, metadata);

        Ok(f.Service.RemoveMedia(failed.Id));
        Ok(f.Service.RemoveMedia(analysed.Id));
        f.UndoRedo.Undo();
        f.UndoRedo.Undo();

        Assert.Equal(new[] { failed, analysed }, f.Project.MediaAssets);
        Assert.Equal(MediaAnalysisStatus.Failed, failed.AnalysisStatus);
        Assert.Equal("unreadable", failed.AnalysisError);
        Assert.Equal(1234L, failed.FileSizeBytes);
        Assert.Same(metadata, analysed.Metadata);
        Assert.Equal(MediaAnalysisStatus.Completed, analysed.AnalysisStatus);
    }

    [Fact]
    public void An_offline_asset_is_removed_like_an_online_one_and_comes_back_offline()
    {
        var f = new TimelineFixture();
        var asset = f.Image("gone.png");
        asset.IsMissing = true;
        f.PlaceImageFrames(f.V1, asset, 0, 25, f.Rate);

        Ok(f.Service.RemoveMedia(asset.Id));
        Assert.Empty(f.Project.MediaAssets);
        Assert.Empty(f.V1.Clips);

        f.UndoRedo.Undo();
        Assert.True(asset.IsMissing);
        Assert.Single(f.V1.Clips);
    }

    [Fact]
    public void The_file_on_disk_is_never_touched()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var f = new TimelineFixture();
            var path = Path.Combine(folder, "keep.png");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var asset = new MediaAsset { FilePath = path, Kind = MediaKind.Image, FileSizeBytes = 3 };
            f.Project.MediaAssets.Add(asset);
            f.PlaceImageFrames(f.V1, asset, 0, 25, f.Rate);
            var written = File.GetLastWriteTimeUtc(path);

            Ok(f.Service.RemoveMedia(asset.Id));
            f.UndoRedo.Undo();
            f.UndoRedo.Redo();

            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_clip_on_a_locked_track_blocks_the_removal()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        Ok(f.Service.AddClip(music.Id));
        f.A1.IsLocked = true;
        var before = State(f);
        var top = f.UndoRedo.CurrentPosition;

        Assert.Equal(1, f.Service.CountClipsUsing(music.Id));          // counted also on a locked track
        Assert.Equal("music.mp3 is used on track A1, which is locked.", f.Service.GetRemoveMediaBlockReason(music.Id));
        var result = f.Service.RemoveMedia(music.Id);

        Assert.False(result.Success);
        Assert.Equal("music.mp3 is used on track A1, which is locked.", result.Message);
        Assert.Equal(before, State(f));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void A_locked_track_without_its_clips_does_not_block()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        var image = f.Image();
        Ok(f.Service.AddClip(music.Id));
        f.A1.IsLocked = true;

        Ok(f.Service.RemoveMedia(image.Id));
    }

    [Fact]
    public void An_asset_that_is_not_in_the_project_is_reported()
    {
        var f = new TimelineFixture();
        var asset = f.Image();
        Ok(f.Service.RemoveMedia(asset.Id));

        var again = f.Service.RemoveMedia(asset.Id);

        Assert.False(again.Success);
        Assert.Equal("That media is no longer in the project.", again.Message);
        Assert.Equal("That media is no longer in the project.", f.Service.GetRemoveMediaBlockReason(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_removal_survives_save_and_reopen_in_format_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            var removed = f.Image("removed.png");
            var kept = f.Image("kept.png");
            f.PlaceImageFrames(f.V1, removed, 0, 25, f.Rate);
            f.PlaceImageFrames(f.V1, kept, 25, 50, f.Rate);
            Ok(f.Service.RemoveMedia(removed.Id));
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            var json = File.ReadAllText(Path.Combine(folder, "project.json"));
            Assert.Contains("\"formatVersion\": 3", json);
            Assert.DoesNotContain("removed.png", json);

            var reopened = await f.Projects.OpenAsync(folder);

            Assert.Equal(new[] { kept.Id }, reopened.MediaAssets.Select(a => a.Id));
            Assert.Equal(kept.Id, ((MediaBackedClip)reopened.Timeline.VideoTracks[0].Clips.Single()).MediaAssetId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
