using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 27 (D043 §1–§6): a new text's defaults (bold, a black 60 % background, «Выезд» from below over a 0.4 s fade in,
/// «Проявление» over a 0.3 s fade out, placed at the bottom centre by its measured box — one Undo step); the style and the
/// motions through <c>SetClipProperties</c> (consecutive changes of one value merge, a gesture is a step of its own, a kind
/// with its duration is one step, Undo / Redo restore every value); the limits; split, duplicate and paste copy the style
/// and the motions while the fades follow D025.
/// </summary>
public class TextStyleEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);

    /// <summary>The number of undo steps (undone to the start and redone back).</summary>
    private static int Depth(TimelineFixture f)
    {
        var n = 0;
        for (; f.UndoRedo.CanUndo; n++) f.UndoRedo.Undo();
        for (var i = 0; i < n; i++) f.UndoRedo.Redo();
        return n;
    }

    private static TextClip NewText(TimelineFixture f, Func<TextProperties, (double, double)>? measure = null, long frame = 0)
    {
        var result = f.Service.AddTextClip(F(f, frame), measure);
        Ok(result);
        return (TextClip)f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
    }

    [Theory]
    [InlineData(25, 10, 8)]       // 0.4 s = 10 frames, 0.3 s = 7.5 → 8
    [InlineData(30, 12, 9)]
    [InlineData(60, 24, 18)]
    public void A_new_text_has_the_D043_defaults(int fps, long inFrames, long outFrames)
    {
        var f = new TimelineFixture();
        Ok(f.Service.SetFrameRate(new FrameRate(fps, 1)));
        var text = NewText(f);

        Assert.True(text.Bold);
        Assert.Equal("#FFFFFF", text.ColorHex);
        Assert.Equal(TextAlignment.Center, text.Alignment);
        Assert.Equal(new TextBackground(true, "#000000", 0.6), text.Background);
        Assert.Equal(TextOutline.Default, text.Outline);
        Assert.Equal(new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom), text.MotionIn);
        Assert.Equal(TextMotion.None, text.MotionOut);
        Assert.Equal(MediaTime.FromFrame(inFrames, f.Rate), text.FadeIn);
        Assert.Equal(MediaTime.FromFrame(outFrames, f.Rate), text.FadeOut);
        Assert.Equal("Add Text", Top(f));
    }

    [Fact]
    public void A_measured_new_text_is_placed_at_the_bottom_centre_in_the_same_step()
    {
        var f = new TimelineFixture();
        TextProperties? measured = null;
        var text = NewText(f, t => { measured = t; return (300, 64); });

        var canvas = new FrameSize(f.Settings.FrameWidth, f.Settings.FrameHeight);
        var expected = TextPlacement.Position(canvas, measured!.Value, VisualProperties.Default, 300, 64, TextGridRow.Bottom, TextGridColumn.Centre);
        Assert.Equal(expected, (text.PositionX, text.PositionY));
        Assert.Equal(0, text.PositionX);
        Assert.True(text.PositionY > 0);
        Assert.True(measured.Value.Bold && measured.Value.Background.IsEnabled);

        f.UndoRedo.Undo();
        Assert.Empty(f.V1.Clips);
    }

    [Fact]
    public void An_unmeasured_new_text_stays_centred() => Assert.Equal((0.0, 0.0), (NewText(new TimelineFixture()).PositionX, NewText(new TimelineFixture()).PositionY));

    private static TimelineEditResult SetText(TimelineFixture f, TextClip clip, Func<TextProperties, TextProperties> change,
        FadeProperties? fade = null, ClipPropertyGesture? gesture = null) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = change(TextProperties.Of(clip)!.Value), Fade = fade, Gesture = gesture });

    [Fact]
    public void A_kind_with_its_duration_is_one_step_and_the_duration_then_merges_on_its_own()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        var steps = Depth(f);

        Ok(SetText(f, clip, t => t with { MotionIn = new TextMotion(TextMotionKind.Zoom) }, FadeProperties.Of(clip) with { FadeIn = F(f, 15) },
            new ClipPropertyGesture("Change Appearance")));
        Assert.Equal(steps + 1, Depth(f));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = FadeProperties.Of(clip) with { FadeIn = F(f, 20) } }));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Fade = FadeProperties.Of(clip) with { FadeIn = F(f, 25) } }));
        Assert.Equal(steps + 2, Depth(f));                      // the two durations merged

        f.UndoRedo.Undo();
        Assert.Equal(F(f, 15), clip.FadeIn);
        f.UndoRedo.Undo();
        Assert.Equal(new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom), clip.MotionIn);
        f.UndoRedo.Redo();
        f.UndoRedo.Redo();
        Assert.Equal((new TextMotion(TextMotionKind.Zoom), F(f, 25)), (clip.MotionIn, clip.FadeIn));
    }

    [Fact]
    public void Discrete_choices_with_a_gesture_never_merge_typed_values_do()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        var steps = Depth(f);

        Ok(SetText(f, clip, t => t with { ColorHex = "#FFEE00" }, gesture: new ClipPropertyGesture("Change Text Color")));
        Ok(SetText(f, clip, t => t with { ColorHex = "#FF0000" }, gesture: new ClipPropertyGesture("Change Text Color")));
        Assert.Equal(steps + 2, Depth(f));

        Ok(SetText(f, clip, t => t with { Outline = t.Outline with { Thickness = 0.10 } }));
        Ok(SetText(f, clip, t => t with { Outline = t.Outline with { Thickness = 0.12 } }));
        Assert.Equal(steps + 3, Depth(f));
        Assert.Equal("Change Outline", Top(f));
    }

    [Theory]
    [InlineData(0.019)]
    [InlineData(0.201)]
    [InlineData(double.NaN)]
    public void An_outline_thickness_outside_the_limits_is_refused(double thickness)
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        var r = SetText(f, clip, t => t with { Outline = t.Outline with { Thickness = thickness } });
        Assert.False(r.Success);
        Assert.Equal(TextOutline.DefaultThickness, clip.Outline.Thickness);
    }

    [Fact]
    public void A_background_opacity_outside_the_limits_or_a_bad_colour_is_refused()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        Assert.False(SetText(f, clip, t => t with { Background = t.Background with { Opacity = 1.5 } }).Success);
        Assert.False(SetText(f, clip, t => t with { Outline = t.Outline with { ColorHex = "black" } }).Success);
        Assert.False(SetText(f, clip, t => t with { MotionIn = new TextMotion((TextMotionKind)9) }).Success);
    }

    [Fact]
    public void Undo_and_redo_restore_every_style_value()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        var before = TextProperties.Of(clip);
        var styled = before!.Value with
        {
            Bold = false, Outline = new TextOutline(true, "#123456", 0.15), Background = new TextBackground(false, "#ABCDEF", 0.25),
            MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Right), MotionOut = new TextMotion(TextMotionKind.Zoom)
        };
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = styled }));
        f.UndoRedo.Undo();
        Assert.Equal(before, TextProperties.Of(clip));
        f.UndoRedo.Redo();
        Assert.Equal(styled, TextProperties.Of(clip));
    }

    [Fact]
    public void A_split_keeps_the_style_and_both_motions_on_both_parts_with_the_fades_of_D025()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        Ok(SetText(f, clip, t => t with { MotionOut = new TextMotion(TextMotionKind.Zoom), Outline = t.Outline with { IsEnabled = true } }));
        var style = TextProperties.Of(clip)!.Value;

        Ok(f.Service.Split(F(f, 50), new[] { clip.Id }));
        var parts = f.V1.Clips.OfType<TextClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(style, TextProperties.Of(p)));
        Assert.Equal(MediaTime.Zero, parts[0].FadeOut);
        Assert.Equal(MediaTime.Zero, parts[1].FadeIn);
    }

    [Fact]
    public void A_duplicate_copies_the_style_and_the_motions()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        Ok(SetText(f, clip, t => t with { Bold = false, Background = t.Background with { ColorHex = "#336699" } }));
        var result = f.Service.DuplicateClips(new[] { clip.Id });
        Ok(result);
        var copy = (TextClip)f.V1.Clips.Single(c => c.Id == result.ClipIds[0]);
        Assert.Equal(TextProperties.Of(clip), TextProperties.Of(copy));
        Assert.Equal((clip.FadeIn, clip.FadeOut), (copy.FadeIn, copy.FadeOut));
    }

    [Fact]
    public void A_canvas_change_scales_the_size_and_keeps_the_relative_outline()
    {
        var f = new TimelineFixture();
        var clip = NewText(f);
        Ok(SetText(f, clip, t => t with { Outline = t.Outline with { IsEnabled = true, Thickness = 0.1 } }));
        Ok(f.Service.SetCanvasSize(960, 540));
        Assert.Equal(24, clip.FontSize, 9);
        Assert.Equal(0.1, clip.Outline.Thickness);
    }
}
