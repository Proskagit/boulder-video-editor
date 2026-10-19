using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 26 (D041 §2): <see cref="ITimelineEditService.FrameClip"/> — "Fill frame" / "Fit in frame" write the Scale
/// <c>CompositionMath.FillScale</c> / <c>FitScale</c> give for the canvas and the asset's display size; only the scale
/// changes, one undo step that never merges with a typed scale, refusals change nothing.
/// </summary>
public class FrameClipTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const double PortraitOnLandscape = 1920.0 * 1920 / (1080.0 * 1080);   // 3.160493…

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    private sealed class Scene
    {
        public readonly TimelineFixture F = new();
        public readonly VideoClip Video;

        public Scene(int displayWidth = 1080, int displayHeight = 1920, int? rotation = 0)
        {
            var asset = F.AddAsset("vivo portrait.mp4", MediaKind.Video, new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, Width = displayHeight, Height = displayWidth,
                DisplayRotation = rotation, DisplayWidth = displayWidth, DisplayHeight = displayHeight
            });
            Ok(F.Service.AddClip(asset.Id, F.V1.Id, MediaTime.Zero));
            Video = (VideoClip)F.V1.Clips.Single();
        }

        public VisualProperties Visual => VisualProperties.Of(Video)!.Value;
        public object Steps => F.UndoRedo.CurrentPosition;   // the top of the history: the same object = no new step
        public string? Top => (F.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    }

    [Fact]
    public void Fill_writes_the_cover_scale_of_a_portrait_clip_on_the_default_canvas_as_one_step()
    {
        var s = new Scene();
        var steps = s.Steps;

        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));

        Assert.Equal(PortraitOnLandscape, s.Visual.Scale, 12);
        Assert.Equal(CompositionMath.FillScale(new FrameSize(1920, 1080), new FrameSize(1080, 1920), VisualProperties.Default), s.Visual.Scale);
        Assert.NotSame(steps, s.Steps);
        Assert.Equal("Fill Frame", s.Top);
        Assert.True(CompositionMath.Layout(new FrameSize(1920, 1080), new FrameSize(1080, 1920), s.Visual).CoversCanvas);

        s.F.UndoRedo.Undo();
        Assert.Same(steps, s.Steps);                                // one step
        Assert.Equal(1.0, s.Visual.Scale);
        s.F.UndoRedo.Redo();
        Assert.Equal(PortraitOnLandscape, s.Visual.Scale, 12);
        s.F.AssertValid();
    }

    [Fact]
    public void Fit_returns_to_the_whole_picture()
    {
        var s = new Scene();
        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));
        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fit));

        Assert.Equal(1.0, s.Visual.Scale);
        Assert.Equal("Fit in Frame", s.Top);

        s.F.UndoRedo.Undo();                                        // Fit and Fill are two steps
        Assert.Equal(PortraitOnLandscape, s.Visual.Scale, 12);
        s.F.UndoRedo.Undo();
        Assert.Equal(1.0, s.Visual.Scale);
    }

    [Fact]
    public void The_step_never_merges_with_a_typed_scale_before_or_after_it()
    {
        var s = new Scene();
        var steps = s.Steps;
        Ok(s.F.Service.SetClipProperties(s.Video.Id, new ClipPropertyChange { Visual = s.Visual with { Scale = 1.5 } }));
        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));
        Ok(s.F.Service.SetClipProperties(s.Video.Id, new ClipPropertyChange { Visual = s.Visual with { Scale = 2 } }));

        s.F.UndoRedo.Undo();
        Assert.Equal(PortraitOnLandscape, s.Visual.Scale, 12);
        s.F.UndoRedo.Undo();
        Assert.Equal(1.5, s.Visual.Scale);
        s.F.UndoRedo.Undo();
        Assert.Equal(1.0, s.Visual.Scale);
        Assert.Same(steps, s.Steps);                                // three steps
    }

    [Fact]
    public void Two_fills_in_a_row_are_one_step_and_a_message()
    {
        var s = new Scene();
        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));
        var steps = s.Steps;

        var again = s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill);
        Assert.True(again.Success);
        Assert.True(again.NoChange);
        Assert.Equal(Messages.Frame_AlreadyFills, again.Message);
        Assert.Same(steps, s.Steps);

        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fit));
        var fit = s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fit);
        Assert.True(fit.NoChange);
        Assert.Equal(Messages.Frame_AlreadyFits, fit.Message);
    }

    [Fact]
    public void Only_the_scale_changes_position_rotation_crop_and_opacity_stay()
    {
        var s = new Scene();
        var before = new VisualProperties(120, -80, 0.7, 90, 0.6, new CropRect(0.1, 0.05, 0.2, 0.15));
        Ok(s.F.Service.SetClipProperties(s.Video.Id, new ClipPropertyChange { Visual = before }));

        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));

        Assert.Equal(before with { Scale = s.Visual.Scale }, s.Visual);
        Assert.Equal(CompositionMath.FillScale(new FrameSize(1920, 1080), new FrameSize(1080, 1920), before), s.Visual.Scale);
        // Centred, the picture covers; the offset is kept as the user set it (D041 §2).
        Assert.True(CompositionMath.Layout(new FrameSize(1920, 1080), new FrameSize(1080, 1920),
            s.Visual with { PositionX = 0, PositionY = 0 }).CoversCanvas);
    }

    [Fact]
    public void A_landscape_clip_fills_a_portrait_canvas()
    {
        var s = new Scene(3840, 2160);
        Ok(s.F.Service.SetCanvasSize(1080, 1920));

        Ok(s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill));

        Assert.Equal(PortraitOnLandscape, s.Visual.Scale, 12);
        Assert.True(CompositionMath.Layout(new FrameSize(1080, 1920), new FrameSize(3840, 2160), s.Visual).CoversCanvas);
    }

    [Fact]
    public void An_image_clip_fills_too()
    {
        var s = new Scene();
        var image = s.F.AddAsset("wall.png", MediaKind.Image, new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(5), Width = 1000, Height = 1000, DisplayRotation = 0, DisplayWidth = 1000, DisplayHeight = 1000
        });
        Ok(s.F.Service.AddClip(image.Id, s.F.V1.Id, MediaTime.FromSeconds(10)));
        var clip = s.F.V1.Clips.OfType<ImageClip>().Single();

        Ok(s.F.Service.FrameClip(clip.Id, ClipFraming.Fill));

        Assert.Equal(1920.0 / 1080, VisualProperties.Of(clip)!.Value.Scale, 12);
    }

    [Fact]
    public void Text_and_audio_clips_are_refused()
    {
        var s = new Scene();
        Ok(s.F.Service.AddTextClip(MediaTime.FromSeconds(20)));
        var text = s.F.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        var music = s.F.Audio(5);
        Ok(s.F.Service.AddClip(music.Id, s.F.A1.Id, MediaTime.Zero));
        var audio = s.F.A1.Clips.Single();
        var steps = s.Steps;

        Assert.Equal(Messages.Frame_NoPicture, s.F.Service.FrameClip(text.Id, ClipFraming.Fill).Message);
        Assert.Equal(Messages.Frame_NoPicture, s.F.Service.FrameClip(audio.Id, ClipFraming.Fit).Message);
        Assert.False(s.F.Service.FrameClip(Guid.NewGuid(), ClipFraming.Fill).Success);
        Assert.Same(steps, s.Steps);
    }

    [Fact]
    public void A_locked_track_is_refused()
    {
        var s = new Scene();
        Ok(s.F.Service.SetTrackLocked(s.F.V1.Id, true));
        var steps = s.Steps;

        var result = s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill);

        Assert.False(result.Success);
        Assert.Equal(string.Format(Messages.Edit_TrackLocked, s.F.V1.Name), result.Message);
        Assert.Equal(1.0, s.Visual.Scale);
        Assert.Same(steps, s.Steps);
    }

    [Fact]
    public void An_unknown_source_size_is_refused()
    {
        var s = new Scene();
        var asset = s.F.FindAsset(s.Video.MediaAssetId)!;
        asset.Metadata!.DisplayWidth = null;
        var steps = s.Steps;

        var result = s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fit);

        Assert.False(result.Success);
        Assert.Equal(string.Format(Messages.Frame_SizeUnknown, asset.FileName), result.Message);
        Assert.Same(steps, s.Steps);
    }

    [Fact]
    public void A_scale_outside_the_limits_is_refused_not_clamped()
    {
        var s = new Scene();
        Ok(s.F.Service.SetClipProperties(s.Video.Id, new ClipPropertyChange { Visual = s.Visual with { Crop = new CropRect(0, 0.49, 0, 0.49) } }));
        var steps = s.Steps;

        var result = s.F.Service.FrameClip(s.Video.Id, ClipFraming.Fill);

        Assert.False(result.Success);
        Assert.StartsWith(Messages.Frame_ScaleOutOfRange[..10], result.Message);
        Assert.Equal(1.0, s.Visual.Scale);
        Assert.Same(steps, s.Steps);
    }
}
