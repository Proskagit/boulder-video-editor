using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 12 Step 12.6 (D027 §5): copy, paste and duplicate of clips. Copy takes detached copies (timing, speed, source
/// range, every property, text, fades; the same media asset, never a file; no dissolve) and is no project change.
/// Paste puts new clips (new ids) at the playhead with the copied distances, each on the track it came from; duplicate
/// puts them right after the originals. Each is one Undo step; anything that would overlap or break a rule — a locked
/// or deleted track, media no longer in the project, another frame rate — rejects the whole operation and changes
/// nothing. Offline media is pasted like any other.
/// </summary>
public class ClipboardEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static (long, long) Span(TimelineFixture f, Clip c) => (c.TimelineStart.ToNearestFrame(f.Rate), c.TimelineEnd.ToNearestFrame(f.Rate));

    private static TextClip Text(TimelineFixture f, Track track, long start, long end, string text = "T")
    {
        var clip = new TextClip { Text = text, TimelineStart = F(f, start), Duration = F(f, end) - F(f, start) };
        var index = track.Clips.FindIndex(c => c.TimelineStart > clip.TimelineStart);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    private static Track AddTrack(TimelineFixture f, TrackType type)
    {
        Ok(f.Service.AddTrack(type));
        return type == TrackType.Video ? f.Project.Timeline.VideoTracks[^1] : f.Project.Timeline.AudioTracks[^1];
    }

    private static T Pasted<T>(TimelineFixture f, TimelineEditResult result, int index = 0) where T : Clip =>
        (T)f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[index]);

    // --- copy -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Copy_is_no_project_change_and_nothing_or_a_missing_clip_copies_nothing()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 25);
        var top = f.UndoRedo.CurrentPosition;
        var before = f.Snapshot();

        var clipboard = f.Service.CopyClips(new[] { a.Id });

        Assert.NotNull(clipboard);
        Assert.Equal(1, clipboard!.Count);
        Assert.Equal(f.V1.Id, clipboard.Entries[0].TrackId);
        Assert.NotSame(a, clipboard.Entries[0].Clip);                      // a detached copy
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.False(f.Project.IsDirty);
        Assert.Equal(before, f.Snapshot());
        Assert.Null(f.Service.CopyClips(Array.Empty<Guid>()));
        Assert.Null(f.Service.CopyClips(new[] { a.Id, Guid.NewGuid() }));
    }

    // --- paste ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pasted_video_clip_keeps_every_property_and_its_media_and_gets_a_new_id()
    {
        var f = new TimelineFixture();
        var asset = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(asset.Id));
        Ok(f.Service.Split(F(f, 100)));
        var source = (VideoClip)f.V1.Clips[1];                             // [100, 500), SourceIn 4 s
        Ok(f.Service.SetClipSpeed(source.Id, ClipSpeed.FromSteps(40)));    // 2×: [100, 300)
        Ok(f.Service.SetClipProperties(source.Id, new ClipPropertyChange
        {
            Visual = new VisualProperties(30, -20, 0.5, 15, 0.7, new CropRect(0.1, 0, 0.2, 0.05)),
            Audio = new AudioProperties(1.5, true),
            Fade = new FadeProperties(F(f, 10), F(f, 5))
        }));
        var clipboard = f.Service.CopyClips(new[] { source.Id })!;

        var result = f.Service.PasteClips(clipboard, F(f, 300));          // right after it

        Ok(result);
        var copy = Pasted<VideoClip>(f, result);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(asset.Id, copy.MediaAssetId);                          // the same asset, no new file
        Assert.Single(f.Project.MediaAssets);
        Assert.Equal((300L, 500L), Span(f, copy));
        Assert.Equal((source.SourceIn, source.SourceOut, source.Speed), (copy.SourceIn, copy.SourceOut, copy.Speed));
        Assert.Equal(VisualProperties.Of(source), VisualProperties.Of(copy));
        Assert.Equal((source.Volume, source.IsMuted), (copy.Volume, copy.IsMuted));
        Assert.Equal((source.FadeIn, source.FadeOut), (copy.FadeIn, copy.FadeOut));
        Assert.Equal("Paste Clip", Top(f));
        f.AssertValid();
    }

    [Fact]
    public void A_pasted_text_clip_keeps_its_text_and_style()
    {
        var f = new TimelineFixture();
        var title = Text(f, f.V1, 0, 50, "Title\nline 2");
        (title.FontFamily, title.FontSize, title.ColorHex, title.Alignment) = ("Arial", 64, "#FF0000", TextAlignment.Left);

        var result = f.Service.PasteClips(f.Service.CopyClips(new[] { title.Id })!, F(f, 60));

        Ok(result);
        var copy = Pasted<TextClip>(f, result);
        Assert.Equal(TextProperties.Of(title), TextProperties.Of(copy));
        Assert.Equal((60L, 110L), Span(f, copy));
    }

    [Fact]
    public void Clips_of_several_tracks_keep_their_tracks_and_distances_from_the_earliest()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var a = Text(f, f.V1, 10, 20);
        var b = Text(f, v2, 15, 40);
        var c = Text(f, f.V1, 30, 35);
        var clipboard = f.Service.CopyClips(new[] { c.Id, b.Id, a.Id })!;

        var result = f.Service.PasteClips(clipboard, F(f, 100));

        Ok(result);
        Assert.Equal(3, result.ClipIds.Count);
        Assert.Equal("Paste Clips", Top(f));
        Assert.Equal(new[] { (100L, 110L), (120L, 125L) }, f.V1.Clips.Skip(2).Select(x => Span(f, x)));
        Assert.Equal((105L, 130L), Span(f, v2.Clips[1]));
        f.AssertValid();

        f.UndoRedo.Undo();                                                  // one step removes all three
        Assert.Equal(new[] { a, c }, f.V1.Clips);
        Assert.Equal(new[] { b }, v2.Clips);
    }

    [Fact]
    public void A_copy_is_detached_from_later_edits_of_its_clip()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 25, "before");
        var clipboard = f.Service.CopyClips(new[] { a.Id })!;
        Ok(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Text = TextProperties.Of(a)!.Value with { Text = "after" } }));
        Ok(f.Service.DeleteClips(new[] { a.Id }));

        var result = f.Service.PasteClips(clipboard, F(f, 0));

        Ok(result);
        Assert.Equal("before", Pasted<TextClip>(f, result).Text);
    }

    [Fact]
    public void A_dissolve_is_not_copied()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddClip(f.Video(20, FrameRate.Fps25).Id));
        Ok(f.Service.Split(F(f, 100)));
        var (a, b) = (f.V1.Clips[0], f.V1.Clips[1]);
        Ok(f.Service.AddTransition(a.Id, b.Id, F(f, 20)));
        var dissolve = f.V1.Transitions.Single();

        Ok(f.Service.PasteClips(f.Service.CopyClips(new[] { a.Id, b.Id })!, F(f, 600)));

        Assert.Equal(4, f.V1.Clips.Count);
        Assert.Same(dissolve, Assert.Single(f.V1.Transitions));           // the pasted pair meets without a dissolve
        f.AssertValid();
    }

    [Fact]
    public void A_paste_that_would_overlap_is_rejected_whole()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var a = Text(f, f.V1, 0, 20);
        var b = Text(f, v2, 0, 20);
        Text(f, v2, 60, 80);                                                // B's copy at 50 would overlap this
        var clipboard = f.Service.CopyClips(new[] { a.Id, b.Id })!;
        var before = f.Snapshot();
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.PasteClips(clipboard, F(f, 50));

        Assert.False(result.Success);
        Assert.StartsWith("Can't paste:", result.Message);
        Assert.Equal(before, f.Snapshot());                                // not even V1's copy
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void A_locked_target_track_rejects_the_paste_but_copying_from_it_is_allowed()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 20);
        f.V1.IsLocked = true;

        var clipboard = f.Service.CopyClips(new[] { a.Id });                // reading a locked track
        Assert.NotNull(clipboard);
        var result = f.Service.PasteClips(clipboard!, F(f, 50));

        Assert.False(result.Success);
        Assert.Equal("Track V1 is locked.", result.Message);
        Assert.Single(f.V1.Clips);
    }

    [Fact]
    public void A_deleted_track_rejects_the_paste()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var a = Text(f, v2, 0, 20);
        var clipboard = f.Service.CopyClips(new[] { a.Id })!;
        Ok(f.Service.DeleteTrack(v2.Id));

        var result = f.Service.PasteClips(clipboard, F(f, 0));

        Assert.False(result.Success);
        Assert.Equal("Can't paste: the track of a copied clip no longer exists.", result.Message);
    }

    [Fact]
    public void Media_removed_from_the_project_after_the_copy_rejects_the_paste()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        Ok(f.Service.AddClip(music.Id));
        var clipboard = f.Service.CopyClips(new[] { f.A1.Clips[0].Id })!;
        Ok(f.Service.RemoveMedia(music.Id));
        var before = f.Snapshot();

        var result = f.Service.PasteClips(clipboard, F(f, 0));

        Assert.False(result.Success);
        Assert.Equal("Can't paste: the media of a copied clip is no longer in the project.", result.Message);
        Assert.Equal(before, f.Snapshot());

        f.UndoRedo.Undo();                                                  // the media is back: the paste works
        Ok(f.Service.PasteClips(clipboard, F(f, 400)));
    }

    [Fact]
    public void Offline_media_is_pasted_like_any_other()
    {
        var f = new TimelineFixture();
        var music = f.Audio(10);
        Ok(f.Service.AddClip(music.Id));
        var clipboard = f.Service.CopyClips(new[] { f.A1.Clips[0].Id })!;
        music.IsMissing = true;

        var result = f.Service.PasteClips(clipboard, F(f, 400));

        Ok(result);
        Assert.Equal(music.Id, Pasted<AudioClip>(f, result).MediaAssetId);
        Assert.Contains(PlaybackSnapshotBuilder.Build(f.Project, 1).AudioSpans, s => s.ClipId == result.ClipIds[0]);
    }

    [Fact]
    public void Another_frame_rate_since_the_copy_rejects_the_paste()
    {
        var f = new TimelineFixture();                                      // provisional 30 fps
        var a = Text(f, f.V1, 0, 30);
        var clipboard = f.Service.CopyClips(new[] { a.Id })!;
        Ok(f.Service.AddClip(f.Video(4, FrameRate.Fps25).Id, start: F(f, 60)));   // the first video fixes 25 fps

        var result = f.Service.PasteClips(clipboard, MediaTime.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Equal("The project frame rate changed since the clips were copied. Copy them again.", result.Message);
    }

    [Fact]
    public void Paste_snaps_to_the_frame_grid_and_the_snapshot_has_the_new_clip()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 10);

        var result = f.Service.PasteClips(f.Service.CopyClips(new[] { a.Id })!, new MediaTime(F(f, 20).Ticks + 1));

        Ok(result);
        Assert.Equal((20L, 30L), Span(f, Pasted<TextClip>(f, result)));
        Assert.True(Pasted<TextClip>(f, result).TimelineStart.IsOnFrameGrid(f.Rate));
        Assert.Contains(PlaybackSnapshotBuilder.Build(f.Project, 1).VideoLayers.Single().Texts, t => t.ClipId == result.ClipIds[0]);
    }

    [Fact]
    public async Task Pasted_clips_survive_save_and_reopen_in_format_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            var a = Text(f, f.V1, 0, 20, "pasted");
            a.FadeIn = F(f, 5);
            var result = f.Service.PasteClips(f.Service.CopyClips(new[] { a.Id })!, F(f, 30));
            Ok(result);
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            Assert.Contains("\"formatVersion\": 3", File.ReadAllText(Path.Combine(folder, "project.json")));

            var reopened = await f.Projects.OpenAsync(folder);

            var copy = (TextClip)reopened.Timeline.VideoTracks[0].Clips.Single(c => c.Id == result.ClipIds[0]);
            Assert.Equal(("pasted", F(f, 5)), (copy.Text, copy.FadeIn));
            Assert.Equal((30L, 50L), Span(f, copy));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // --- duplicate ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_puts_the_copies_right_after_the_selection_on_their_tracks_in_one_step()
    {
        var f = new TimelineFixture();
        var v2 = AddTrack(f, TrackType.Video);
        var a = Text(f, f.V1, 0, 20);
        var b = Text(f, v2, 10, 40);
        var before = f.Snapshot();

        var result = f.Service.DuplicateClips(new[] { a.Id, b.Id });

        Ok(result);
        Assert.Equal("Duplicate Clips", Top(f));
        Assert.Equal((40L, 60L), Span(f, f.V1.Clips[1]));                  // the earliest copy where the last one ends
        Assert.Equal((50L, 80L), Span(f, v2.Clips[1]));
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Duplicate_is_rejected_when_a_copy_would_overlap_or_the_track_is_locked()
    {
        var f = new TimelineFixture();
        var a = Text(f, f.V1, 0, 20);
        Text(f, f.V1, 30, 40);                                              // A's copy at 20 would overlap it
        var before = f.Snapshot();

        var overlap = f.Service.DuplicateClips(new[] { a.Id });
        f.V1.IsLocked = true;
        var locked = f.Service.DuplicateClips(new[] { a.Id });

        Assert.StartsWith("Can't duplicate:", overlap.Message);
        Assert.Equal("Track V1 is locked.", locked.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal("A selected clip no longer exists.", f.Service.DuplicateClips(new[] { Guid.NewGuid() }).Message);
        Assert.True(f.Service.DuplicateClips(Array.Empty<Guid>()).NoChange);
    }
}
