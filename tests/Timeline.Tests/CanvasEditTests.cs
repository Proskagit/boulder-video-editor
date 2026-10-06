using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 13 Step 13.4 (D028, CS-1 B): <see cref="ITimelineEditService.SetCanvasSize"/>. Width and height change together
/// as one Undo step; with <c>s = min(W'/W, H'/H)</c> the positions of every video / image / text clip and the font size
/// of every text clip are multiplied by <c>s</c> — on locked and hidden tracks too — and nothing else changes. A size
/// the rules refuse, or a scaled value outside the property limits, changes nothing. Undo / Redo write back the stored
/// values; a change of the size and back again need not restore them.
/// </summary>
public class CanvasEditTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static (int, int) Canvas(TimelineFixture f) => (f.Settings.FrameWidth, f.Settings.FrameHeight);

    /// <summary>A 1920 × 1080 project at 25 fps: a full-frame video on V1 (moved, scaled, rotated, cropped, faded), a
    /// text clip and an image on V2, a dissolve between two V1 clips, an audio clip, a marker and a playhead.</summary>
    private sealed class Scene
    {
        public readonly TimelineFixture F = new();
        public readonly VideoClip Video;
        public readonly VideoClip Second;
        public readonly TextClip Title;
        public readonly ImageClip Logo;
        public readonly AudioClip Music;
        public readonly Track V2;

        public Scene()
        {
            var video = F.AddAsset("pattern.mp4", MediaKind.Video, new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            });
            Ok(F.Service.AddClip(video.Id, F.V1.Id, MediaTime.Zero));
            Ok(F.Service.Split(Frame(100)));                             // two V1 clips that meet, with handles
            Video = (VideoClip)F.V1.Clips[0];
            Second = (VideoClip)F.V1.Clips[1];
            Ok(F.Service.AddTransition(Video.Id, Second.Id, Frame(10)));

            Ok(F.Service.AddTrack(TrackType.Video));
            V2 = F.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First();
            Ok(F.Service.AddTextClip(Frame(10)));
            Title = (TextClip)V2.Clips.Single();
            var logo = F.Image("logo.png");
            logo.AnalysisStatus = MediaAnalysisStatus.Completed;
            Ok(F.Service.AddClip(logo.Id, V2.Id, Frame(200)));
            Logo = (ImageClip)V2.Clips.Single(c => c is ImageClip);

            var music = F.Audio(20);
            Ok(F.Service.AddClip(music.Id, F.A1.Id, MediaTime.Zero));
            Music = (AudioClip)F.A1.Clips.Single();

            Set(Video, new VisualProperties(-768, -432, 0.8, 15, 0.9, new CropRect(0.1, 0, 0.05, 0.2)));
            Ok(F.Service.SetClipProperties(Video.Id, new ClipPropertyChange { Fade = new FadeProperties(Frame(5), Frame(7)) }));
            Set(Title, new VisualProperties(0, 400, 1.5, -10, 1, CropRect.None));
            Ok(F.Service.SetClipProperties(Title.Id, new ClipPropertyChange { Text = TextProperties.Of(Title)!.Value with { FontSize = 72 } }));
            Set(Logo, new VisualProperties(768, -432, 0.25, 0, 1, CropRect.None));
            Ok(F.Service.AddMarker(Frame(50)));
            F.Project.Timeline.PlayheadPosition = Frame(42);
            MarkSaved();
        }

        public void MarkSaved() => F.UndoRedo.MarkSavePoint(F.UndoRedo.CurrentPosition);

        public static MediaTime Frame(long n) => MediaTime.FromFrame(n, Rate);

        public void Set(Clip clip, VisualProperties visual) =>
            Ok(F.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual }));

        /// <summary>Everything that must stay as it is: timing, fades, dissolves (the fixture's snapshot), markers, the
        /// playhead, the rate, and every property that isn't kept in canvas pixels.</summary>
        public string Unscaled() => string.Join("\n",
            F.Snapshot(),
            string.Join(",", F.Project.Timeline.Markers.Select(m => $"{m.Id}@{m.Position.Ticks}")),
            F.Project.Timeline.PlayheadPosition.Ticks,
            string.Join(";", F.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Select(c =>
            {
                var v = VisualProperties.Of(c)!.Value;
                var t = TextProperties.Of(c);
                return $"{c.Id}:{v.Scale},{v.RotationDegrees},{v.Opacity},{v.Crop},{t?.Text},{t?.FontFamily},{t?.ColorHex},{t?.Alignment}";
            })),
            $"{Music.Volume},{Music.IsMuted}");
    }

    private static VisualProperties V(Clip clip) => VisualProperties.Of(clip)!.Value;

    [Fact]
    public void A_canvas_change_is_one_undo_step_that_scales_positions_and_font_sizes_and_nothing_else()
    {
        var s = new Scene();
        var f = s.F;
        var unscaled = s.Unscaled();
        var changes = f.TimelineChangedCount;
        var mediaChanges = 0;
        f.Projects.MediaAssetsChanged += (_, _) => mediaChanges++;

        var result = f.Service.SetCanvasSize(3840, 2160);

        Ok(result);
        Assert.Equal((3840, 2160), Canvas(f));
        Assert.Equal((-1536.0, -864.0), (V(s.Video).PositionX, V(s.Video).PositionY));
        Assert.Equal((0.0, 800.0), (V(s.Title).PositionX, V(s.Title).PositionY));
        Assert.Equal(144.0, s.Title.FontSize);
        Assert.Equal((1536.0, -864.0), (V(s.Logo).PositionX, V(s.Logo).PositionY));
        Assert.Equal(unscaled, s.Unscaled());
        Assert.Equal(Rate, f.Settings.FrameRate);
        Assert.Equal("Set Frame Size", Top(f));
        Assert.True(f.Project.IsDirty);
        Assert.Equal(changes + 1, f.TimelineChangedCount);              // one notification for the whole change
        Assert.Equal(0, mediaChanges);                                  // no media event: thumbnails / waveforms stay
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal((1920, 1080), Canvas(f));
        Assert.Equal((-768.0, -432.0), (V(s.Video).PositionX, V(s.Video).PositionY));
        Assert.Equal(72.0, s.Title.FontSize);
        Assert.Equal((768.0, -432.0), (V(s.Logo).PositionX, V(s.Logo).PositionY));
        Assert.Equal(unscaled, s.Unscaled());
        Assert.False(f.Project.IsDirty);                                // back at the save point
        Assert.Equal(changes + 2, f.TimelineChangedCount);

        f.UndoRedo.Redo();
        Assert.Equal((3840, 2160), Canvas(f));
        Assert.Equal((-1536.0, -864.0), (V(s.Video).PositionX, V(s.Video).PositionY));
        Assert.Equal(144.0, s.Title.FontSize);
        Assert.Equal(unscaled, s.Unscaled());
        Assert.True(f.Project.IsDirty);
    }

    public static TheoryData<int, int, int, int, double> Transitions => new()
    {
        { 1920, 1080, 3840, 2160, 2 },
        { 1920, 1080, 1080, 1920, 0.5625 },
        { 1920, 1080, 1080, 1080, 0.5625 },
        { 3840, 2160, 1920, 1080, 0.5 },
        { 1080, 1920, 1920, 1080, 0.5625 },
        { 1920, 1080, 1280, 720, 2.0 / 3 },
        { 1920, 1080, 1920, 1200, 1 },                                  // wider and taller: the old canvas fits as it is
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Positions_and_font_sizes_scale_by_the_contain_factor_of_the_old_canvas(int w, int h, int w2, int h2, double factor)
    {
        var s = new Scene();
        var f = s.F;
        f.Settings.FrameWidth = w;                                      // the scene's starting canvas (as loaded)
        f.Settings.FrameHeight = h;
        var centre = new VisualProperties(0, 0, 1, 0, 1, CropRect.None);
        s.Set(s.Video, centre);
        s.Set(s.Title, new VisualProperties(-0.4 * w, 0.4 * h, 1, 0, 1, CropRect.None));   // near the bottom-left corner
        s.Set(s.Logo, new VisualProperties(0.4 * w, -0.4 * h, 0.25, 0, 1, CropRect.None));  // near the top-right corner
        var (title, logo) = (V(s.Title), V(s.Logo));

        Ok(f.Service.SetCanvasSize(w2, h2));

        Assert.Equal((w2, h2), Canvas(f));
        Assert.Equal(centre, V(s.Video));                               // the centre stays the centre
        Assert.Equal((title.PositionX * factor, title.PositionY * factor), (V(s.Title).PositionX, V(s.Title).PositionY));
        Assert.Equal((logo.PositionX * factor, logo.PositionY * factor), (V(s.Logo).PositionX, V(s.Logo).PositionY));
        Assert.Equal(72 * factor, s.Title.FontSize);

        // The old canvas, fitted into the new one, keeps every positioned element on the canvas.
        foreach (var clip in new Clip[] { s.Title, s.Logo })
        {
            var v = V(clip);
            Assert.InRange(w2 / 2.0 + v.PositionX, 0, w2);
            Assert.InRange(h2 / 2.0 + v.PositionY, 0, h2);
        }
    }

    [Fact]
    public void Changing_the_size_and_back_again_does_not_restore_the_values_only_undo_does()
    {
        // CS-1 B, accepted as such (D028 Step 13.4): s is the "contain" factor in both directions, so 16:9 → 9:16 → 16:9
        // multiplies by 0.5625² = 0.31640625. No hidden history restores the old values; Undo does, exactly.
        var s = new Scene();
        var f = s.F;

        Ok(f.Service.SetCanvasSize(1080, 1920));
        Ok(f.Service.SetCanvasSize(1920, 1080));

        Assert.Equal((1920, 1080), Canvas(f));
        Assert.Equal((-768 * 0.31640625, -432 * 0.31640625), (V(s.Video).PositionX, V(s.Video).PositionY));
        Assert.Equal(72 * 0.31640625, s.Title.FontSize);

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal((-768.0, -432.0), (V(s.Video).PositionX, V(s.Video).PositionY));
        Assert.Equal(72.0, s.Title.FontSize);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void The_same_size_changes_nothing_and_leaves_no_undo_step()
    {
        var s = new Scene();
        var f = s.F;
        var (top, changes) = (f.UndoRedo.CurrentPosition, f.TimelineChangedCount);

        var result = f.Service.SetCanvasSize(1920, 1080);

        Assert.True(result.Success);
        Assert.True(result.NoChange);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Theory]
    [InlineData(1921, 1080, "even")]
    [InlineData(1920, 1081, "even")]
    [InlineData(62, 1080, "between 64 and 4096")]
    [InlineData(4098, 2160, "between 64 and 4096")]
    [InlineData(0, 0, "between 64 and 4096")]
    [InlineData(4096, 4096, "too large")]
    public void A_size_the_rules_refuse_changes_nothing(int width, int height, string reason)
    {
        var s = new Scene();
        var f = s.F;
        var (top, changes, before) = (f.UndoRedo.CurrentPosition, f.TimelineChangedCount, s.Unscaled());
        var position = V(s.Video);

        var result = f.Service.SetCanvasSize(width, height);

        Assert.False(result.Success);
        Assert.Contains(reason, result.Message);
        Assert.Equal(ProjectSettingsRules.CanvasError(width, height), result.Message);
        Assert.Equal((1920, 1080), Canvas(f));
        Assert.Equal(position, V(s.Video));
        Assert.Equal(before, s.Unscaled());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void The_largest_allowed_canvas_is_accepted()
    {
        var s = new Scene();
        Ok(s.F.Service.SetCanvasSize(4096, 2304));
        Assert.Equal((4096, 2304), Canvas(s.F));
    }

    [Theory]
    [InlineData("font size above 1000", 3840, 2160, "Font size", "the text clip \"Text\"")]
    [InlineData("font size below 1", 1280, 720, "Font size", "the text clip \"Text\"")]
    [InlineData("position beyond 100 000", 3840, 2160, "Position", "the image clip of logo.png")]
    public void A_scaled_value_outside_the_limits_refuses_the_whole_change(string what, int width, int height, string property, string clipName)
    {
        var s = new Scene();
        var f = s.F;
        switch (what)
        {
            case "font size above 1000":
                Ok(f.Service.SetClipProperties(s.Title.Id, new ClipPropertyChange { Text = TextProperties.Of(s.Title)!.Value with { FontSize = 600 } }));
                break;
            case "font size below 1":
                Ok(f.Service.SetClipProperties(s.Title.Id, new ClipPropertyChange { Text = TextProperties.Of(s.Title)!.Value with { FontSize = 1.2 } }));
                break;
            default:
                s.Set(s.Logo, V(s.Logo) with { PositionX = 60_000 });
                break;
        }
        s.MarkSaved();
        var values = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Select(c => (V(c), TextProperties.Of(c))).ToList();
        var (top, changes, before) = (f.UndoRedo.CurrentPosition, f.TimelineChangedCount, s.Unscaled());

        var result = f.Service.SetCanvasSize(width, height);

        Assert.False(result.Success, what);
        Assert.Contains(property, result.Message);
        Assert.Contains("track V2", result.Message);                    // names the clip's track …
        Assert.Contains(clipName, result.Message);                      // … and the clip
        Assert.Equal((1920, 1080), Canvas(f));
        Assert.Equal(values, f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Select(c => (V(c), TextProperties.Of(c))).ToList());
        Assert.Equal(before, s.Unscaled());
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void Locked_and_hidden_tracks_do_not_block_the_change_and_their_clips_are_scaled_too()
    {
        var s = new Scene();
        var f = s.F;
        s.V2.IsLocked = true;
        f.V1.IsHidden = true;

        Ok(f.Service.SetCanvasSize(3840, 2160));

        Assert.Equal(144.0, s.Title.FontSize);                          // V2, locked
        Assert.Equal(1536.0, V(s.Logo).PositionX);                      // V2, locked
        Assert.Equal(-1536.0, V(s.Video).PositionX);                    // V1, hidden
        Assert.True(s.V2.IsLocked);
        Assert.True(f.V1.IsHidden);
    }

    [Fact]
    public void Without_clips_only_the_canvas_changes()
    {
        var f = new TimelineFixture();

        Ok(f.Service.SetCanvasSize(1080, 1920));

        Assert.Equal((1080, 1920), Canvas(f));
        Assert.Equal("Set Frame Size", Top(f));
        f.UndoRedo.Undo();
        Assert.Equal((1920, 1080), Canvas(f));
    }

    [Fact]
    public void A_project_loaded_with_a_canvas_outside_the_limits_can_be_changed_to_a_valid_one()
    {
        var s = new Scene();
        s.F.Settings.FrameWidth = 1921;                                 // written by hand into the file (CS-2)
        s.F.Settings.FrameHeight = 1081;

        Ok(s.F.Service.SetCanvasSize(1920, 1080));

        Assert.Equal((1920, 1080), Canvas(s.F));
        Assert.Equal(-768 * (1080.0 / 1081), V(s.Video).PositionX);     // 1920 · 1081 > 1080 · 1921: the height decides
    }

    // --- clipboard (B-4) ----------------------------------------------------------------------------------------------

    [Fact]
    public void Clips_copied_before_a_canvas_change_are_refused_on_paste_until_copied_again()
    {
        var s = new Scene();
        var f = s.F;
        var clipboard = f.Service.CopyClips(new[] { s.Title.Id })!;
        Assert.Equal(new FrameSize(1920, 1080), clipboard.Canvas);
        Ok(f.Service.SetCanvasSize(3840, 2160));
        var (top, count) = (f.UndoRedo.CurrentPosition, s.V2.Clips.Count);

        var paste = f.Service.PasteClips(clipboard, Scene.Frame(400));

        Assert.False(paste.Success);
        Assert.Equal("The project frame size changed since the clips were copied. Copy them again.", paste.Message);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(count, s.V2.Clips.Count);

        var again = f.Service.CopyClips(new[] { s.Title.Id })!;         // copied at the new size: pasted as it is now
        Ok(f.Service.PasteClips(again, Scene.Frame(400)));
        var pasted = (TextClip)s.V2.Clips.Single(c => c.TimelineStart == Scene.Frame(400));
        Assert.Equal(144.0, pasted.FontSize);
        Assert.Equal(800.0, pasted.PositionY);
    }

    [Fact]
    public void Undoing_the_canvas_change_makes_the_old_clipboard_valid_again()
    {
        var s = new Scene();
        var f = s.F;
        var clipboard = f.Service.CopyClips(new[] { s.Title.Id })!;
        Ok(f.Service.SetCanvasSize(3840, 2160));
        f.UndoRedo.Undo();

        Ok(f.Service.PasteClips(clipboard, Scene.Frame(400)));
    }

    // --- snapshot and persistence ---------------------------------------------------------------------------------

    [Fact]
    public void The_snapshot_has_the_new_canvas_and_differs_only_in_presentation()
    {
        var s = new Scene();
        var f = s.F;
        var before = PlaybackSnapshotBuilder.Build(f.Project, version: 1);

        Ok(f.Service.SetCanvasSize(1080, 1920));
        var after = PlaybackSnapshotBuilder.Build(f.Project, version: 2);

        Assert.Equal(new FrameSize(1920, 1080), before.Canvas);
        Assert.Equal(new FrameSize(1080, 1920), after.Canvas);
        Assert.True(after.DiffersOnlyInPresentation(before));           // playback keeps every decoder
        Assert.True(before.DiffersOnlyInPresentation(after));

        // The text layer is drawn with the scaled font size and position on the new canvas.
        var text = Assert.Single(after.LayersAt(Scene.Frame(20)).OfType<TextLayer>());
        Assert.Equal(72 * 0.5625, text.Text.FontSize);
        Assert.Equal(CompositionMath.TextTransform(new FrameSize(1080, 1920), V(s.Title)), text.Transform);
        Assert.Equal(new PointD(540, 960 + 400 * 0.5625), text.Transform.Apply(new PointD(0, 0)));
    }

    [Fact]
    public void Picture_layout_uses_the_new_canvas_fit_and_the_scaled_position()
    {
        var s = new Scene();
        var f = s.F;
        s.Set(s.Video, new VisualProperties(-768, -432, 1, 0, 1, CropRect.None));

        Ok(f.Service.SetCanvasSize(1080, 1920));
        var layer = PlaybackSnapshotBuilder.Build(f.Project, version: 1).LayersAt(Scene.Frame(20)).OfType<PictureLayer>()
            .Single(l => l.ClipId == s.Video.Id);

        var geometry = layer.Geometry!;
        Assert.Equal(new FrameSize(1080, 1920), geometry.Canvas);
        Assert.Equal(0.5625, geometry.FitScale);                        // a 1920 × 1080 source contained in 1080 × 1920
        Assert.Equal(new PointD(540 - 432, 960 - 243), geometry.Center);
    }

    [Fact]
    public void The_new_canvas_and_the_scaled_values_survive_save_and_reopen()
    {
        var s = new Scene();
        Ok(s.F.Service.SetCanvasSize(1080, 1080));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(s.F.Project, @"C:\P"), @"C:\P", _ => true);

        Assert.Equal((1080, 1080), (loaded.Settings.FrameWidth, loaded.Settings.FrameHeight));
        var title = loaded.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        Assert.Equal(72 * 0.5625, title.FontSize);
        Assert.Equal(400 * 0.5625, title.PositionY);
    }
}
