using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 29 (D045 §3, §5–§6): a template is added at the playhead on the topmost video track with its kind's length,
/// fields and appearance / disappearance (the clip's fades) in one undo step; its fields are edited with the usual merging
/// (a typed field merges, a discrete choice is its own step) and validated; Split keeps the appearance on the left part,
/// the disappearance on the right part and continues the counter; Duplicate and copy / paste keep every field; a template
/// takes no transition and no other clip's properties.
/// </summary>
public class TemplateEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    private static TemplateClip Add(TimelineFixture f, TemplateKind kind, long atFrame = 0)
    {
        var result = f.Service.AddTemplateClip(kind, MediaTime.FromFrame(atFrame, f.Rate));
        Ok(result);
        return (TemplateClip)f.V1.Clips.Single(c => c.Id == result.ClipIds!.Single());
    }

    public static TheoryData<TemplateKind, double, double, double> Kinds => new()
    {
        { TemplateKind.Rec, 10, 0.2, 0.2 },
        { TemplateKind.Vhs, 10, 0, 0 },
        { TemplateKind.Boulder, 8, 0.35, 0.3 },
        { TemplateKind.Highlight, 3, 0.6, 0.3 }
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_template_is_added_with_its_kind_s_defaults_in_one_undo_step(TemplateKind kind, double seconds, double fadeIn, double fadeOut)
    {
        var f = new TimelineFixture();
        var before = f.Snapshot();

        var clip = Add(f, kind, 15);

        var rate = f.Rate;
        Assert.Equal(MediaTime.FromFrame(15, rate), clip.TimelineStart);
        Assert.Equal(MediaTime.FromSeconds(seconds).ToNearestFrame(rate), clip.Duration.ToNearestFrame(rate));
        Assert.Equal(MediaTime.FromSeconds(fadeIn).ToNearestFrame(rate), clip.FadeIn.ToNearestFrame(rate));
        Assert.Equal(MediaTime.FromSeconds(fadeOut).ToNearestFrame(rate), clip.FadeOut.ToNearestFrame(rate));
        Assert.True(clip.FadeIn.IsOnFrameGrid(rate) && clip.FadeOut.IsOnFrameGrid(rate));
        Assert.Equal(TemplateRules.Defaults(kind), TemplateProperties.Of(clip));
        Assert.Equal(MediaTime.Zero, clip.CounterStart);
        Assert.Equal(kind == TemplateKind.Highlight ? Messages.Template_DefaultTitle : "", clip.Title);
        Assert.Equal("Add Template", Top(f));
        f.AssertValid();

        var after = f.Snapshot();
        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
    }

    [Fact]
    public void It_goes_on_the_topmost_video_track_and_makes_room_there()
    {
        var f = new TimelineFixture();
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        f.Project.Timeline.VideoTracks.Add(v2);
        var image = f.Image();
        var existing = f.PlaceImageFrames(v2, image, 30, 90, f.Rate);

        var result = f.Service.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromFrame(30, f.Rate));

        Ok(result);
        var template = v2.Clips.OfType<TemplateClip>().Single();
        Assert.Equal(MediaTime.FromFrame(30, f.Rate), template.TimelineStart);
        Assert.Equal(template.TimelineEnd, existing.TimelineStart);      // D037 §3: room made on the right
        Assert.Empty(f.V1.Clips);
        f.AssertValid();
    }

    [Fact]
    public void A_locked_top_track_or_no_video_track_refuses_and_changes_nothing()
    {
        var f = new TimelineFixture();
        f.V1.IsLocked = true;
        var before = f.Snapshot();
        var locked = f.Service.AddTemplateClip(TemplateKind.Rec, MediaTime.Zero);
        Assert.False(locked.Success);
        Assert.Equal(string.Format(Messages.Edit_TrackLocked, f.V1.Name), locked.Message);
        Assert.Equal(before, f.Snapshot());

        f.Project.Timeline.VideoTracks.Clear();
        Assert.Equal(Messages.Edit_NoVideoTrackPlain, f.Service.AddTemplateClip(TemplateKind.Rec, MediaTime.Zero).Message);
    }

    [Fact]
    public void Typing_into_a_field_merges_and_a_status_click_is_its_own_step()
    {
        var f = new TimelineFixture();
        var clip = Add(f, TemplateKind.Boulder);
        var start = TemplateProperties.Of(clip)!.Value;

        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = start with { Route = "7" } }));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = start with { Route = "7A" } }));
        Assert.Equal("Change Template", Top(f));
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Template = start with { Route = "7A", Status = BoulderStatus.Top }, Gesture = new ClipPropertyGesture("Change Route Status")
        }));
        Assert.Equal(("7A", BoulderStatus.Top), (clip.Route, clip.Status));

        f.UndoRedo.Undo();                                   // the status
        Assert.Equal(("7A", BoulderStatus.None), (clip.Route, clip.Status));
        f.UndoRedo.Undo();                                   // both route edits, merged (D017)
        Assert.Equal("", clip.Route);
        f.UndoRedo.Undo();                                   // the add
        Assert.Empty(f.V1.Clips);
    }

    [Theory]
    [InlineData("route", 13)]
    [InlineData("grade", 13)]
    [InlineData("date", 25)]
    [InlineData("title", 61)]
    [InlineData("subtitle", 81)]
    public void A_field_longer_than_its_limit_is_refused(string field, int length)
    {
        var f = new TimelineFixture();
        var clip = Add(f, TemplateKind.Boulder);
        var p = TemplateProperties.Of(clip)!.Value;
        var text = new string('x', length);
        var changed = field switch
        {
            "route" => p with { Route = text },
            "grade" => p with { Grade = text },
            "date" => p with { Date = text },
            "title" => p with { Title = text },
            _ => p with { Subtitle = text }
        };
        var before = f.Snapshot();

        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = changed });

        Assert.False(result.Success);
        Assert.Equal(string.Format(Messages.Template_FieldLength, length - 1), result.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(p, TemplateProperties.Of(clip));
    }

    [Fact]
    public void A_line_break_a_bad_colour_or_an_unknown_status_is_refused()
    {
        var f = new TimelineFixture();
        var clip = Add(f, TemplateKind.Highlight);
        var p = TemplateProperties.Of(clip)!.Value;

        Assert.Equal(Messages.Template_OneLine, f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = p with { Title = "A\nB" } }).Message);
        Assert.Equal(Messages.Prop_ColorFormat, f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = p with { AccentHex = "yellow" } }).Message);
        Assert.Equal(Messages.Template_UnknownStatus, f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = p with { Status = (BoulderStatus)9 } }).Message);
    }

    [Fact]
    public void Template_fields_on_another_clip_and_text_or_picture_fields_on_a_template_are_refused()
    {
        var f = new TimelineFixture();
        var image = f.PlaceImageFrames(f.V1, f.Image(), 0, 30, f.Rate);
        var template = Add(f, TemplateKind.Rec, 60);

        Assert.Equal(Messages.Prop_OnlyTemplate, f.Service.SetClipProperties(image.Id,
            new ClipPropertyChange { Template = TemplateRules.Defaults(TemplateKind.Rec) }).Message);
        Assert.Equal(Messages.Prop_OnlyText, f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Text = new TextProperties("x", "Segoe UI", 48, "#FFFFFF", TextAlignment.Center) }).Message);
        Assert.Equal(Messages.Prop_AudioNoPicture, f.Service.SetClipProperties(template.Id,
            new ClipPropertyChange { Visual = VisualProperties.Default }).Message);
    }

    [Fact]
    public void Split_keeps_the_outer_animation_and_continues_the_counter()
    {
        var f = new TimelineFixture();
        var clip = Add(f, TemplateKind.Rec, 30);
        clip.CounterStart = MediaTime.FromFrame(15, f.Rate);   // a counter already running (e.g. a right part)
        var (fadeIn, fadeOut) = (clip.FadeIn, clip.FadeOut);
        var before = f.Snapshot();

        Ok(f.Service.Split(MediaTime.FromFrame(130, f.Rate), new[] { clip.Id }));

        var parts = f.V1.Clips.OfType<TemplateClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal((fadeIn, MediaTime.Zero), (parts[0].FadeIn, parts[0].FadeOut));
        Assert.Equal((MediaTime.Zero, fadeOut), (parts[1].FadeIn, parts[1].FadeOut));
        Assert.Equal(MediaTime.FromFrame(15, f.Rate), parts[0].CounterStart);
        Assert.Equal(MediaTime.FromFrame(15 + 100, f.Rate), parts[1].CounterStart);
        Assert.Equal(TemplateProperties.Of(parts[0]), TemplateProperties.Of(parts[1]));
        Assert.Equal(TemplateKind.Rec, parts[1].Kind);
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(MediaTime.FromFrame(15, f.Rate), clip.CounterStart);
    }

    [Fact]
    public void Duplicate_and_copy_paste_keep_the_kind_the_counter_and_every_field()
    {
        var f = new TimelineFixture();
        var clip = Add(f, TemplateKind.Boulder);
        clip.CounterStart = MediaTime.FromFrame(7, f.Rate);
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(clip)!.Value with { Route = "3", Grade = "6B+", Status = BoulderStatus.Attempt, AccentHex = "#2979FF" }
        }));

        Ok(f.Service.DuplicateClips(new[] { clip.Id }));
        var clipboard = f.Service.CopyClips(new[] { clip.Id })!;
        Ok(f.Service.PasteClips(clipboard, MediaTime.FromFrame(600, f.Rate)));

        var copies = f.V1.Clips.OfType<TemplateClip>().Where(c => c.Id != clip.Id).ToList();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, c =>
        {
            Assert.Equal(TemplateKind.Boulder, c.Kind);
            Assert.Equal(clip.CounterStart, c.CounterStart);
            Assert.Equal(TemplateProperties.Of(clip), TemplateProperties.Of(c));
            Assert.Equal((clip.FadeIn, clip.FadeOut, clip.Duration), (c.FadeIn, c.FadeOut, c.Duration));
        });
        f.AssertValid();
    }

    [Fact]
    public void A_template_takes_no_transition()
    {
        var f = new TimelineFixture();
        var image = f.PlaceImageFrames(f.V1, f.Image(), 0, 30, f.Rate);
        var template = Add(f, TemplateKind.Highlight, 30);
        var before = f.Snapshot();

        Assert.Equal(Messages.Transition_NotOnTemplate, f.Service.AddTransition(image.Id, template.Id, TransitionRules.CrossDissolve).Message);
        Assert.Equal(Messages.Transition_NotOnTemplate, f.Service.AddTransition(image.Id, template.Id, MediaTime.FromFrame(10, f.Rate)).Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void A_template_moves_to_another_video_track_but_never_to_an_audio_track()
    {
        var f = new TimelineFixture();
        var template = Add(f, TemplateKind.Vhs);
        var before = f.Snapshot();

        Assert.False(f.Service.MoveClips(new[] { template.Id }, 0, f.A1.Id).Success);
        Assert.Equal(before, f.Snapshot());
    }

    // --- D045 §17: the moment the status appears ----------------------------------------------------------------------

    private static TemplateClip BoulderWithStatus(TimelineFixture f, long atFrame = 30)
    {
        var clip = Add(f, TemplateKind.Boulder, atFrame);
        Ok(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(clip)!.Value with { Route = "7", Status = BoulderStatus.Top },
            Gesture = new ClipPropertyGesture("Change Route Status")
        }));
        return clip;
    }

    private static TimelineEditResult SetStatusAt(TimelineFixture f, TemplateClip clip, long frame) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Template = TemplateProperties.Of(clip)!.Value with { StatusAt = MediaTime.FromFrame(frame, f.Rate) },
            Gesture = new ClipPropertyGesture("Set Status Moment")
        });

    [Fact]
    public void A_new_template_s_status_moment_is_its_start()
    {
        var f = new TimelineFixture();
        Assert.Equal(MediaTime.Zero, Add(f, TemplateKind.Boulder).StatusAt);
    }

    [Fact]
    public void Setting_or_moving_the_status_moment_is_one_undo_step_each()
    {
        var f = new TimelineFixture();
        var clip = BoulderWithStatus(f);

        Ok(SetStatusAt(f, clip, 60));
        Assert.Equal("Set Status Moment", Top(f));
        Ok(SetStatusAt(f, clip, 90));                        // a second click: its own step, never merged
        Assert.Equal(MediaTime.FromFrame(90, f.Rate), clip.StatusAt);

        f.UndoRedo.Undo();
        Assert.Equal(MediaTime.FromFrame(60, f.Rate), clip.StatusAt);
        f.UndoRedo.Undo();
        Assert.Equal(MediaTime.Zero, clip.StatusAt);
        Assert.Equal(BoulderStatus.Top, clip.Status);       // the status stays: only the moment was undone
        f.UndoRedo.Redo();
        f.UndoRedo.Redo();
        Assert.Equal(MediaTime.FromFrame(90, f.Rate), clip.StatusAt);
    }

    [Fact]
    public void A_negative_status_moment_is_refused()
    {
        var f = new TimelineFixture();
        var clip = BoulderWithStatus(f);
        var p = TemplateProperties.Of(clip)!.Value;

        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Template = p with { StatusAt = new MediaTime(-1) } });

        Assert.False(result.Success);
        Assert.Equal(Messages.Template_StatusAtNegative, result.Message);
        Assert.Equal(p, TemplateProperties.Of(clip));
    }

    [Theory]
    [InlineData(50)]       // the cut after the moment: the left part shows the appearance, the right part the status at once
    [InlineData(150)]      // the cut before it: the right part shows the appearance
    public void Split_keeps_the_absolute_status_moment_in_both_parts(long cutAfter)
    {
        var f = new TimelineFixture();
        var clip = BoulderWithStatus(f, 30);
        Ok(SetStatusAt(f, clip, 100));                       // 100 frames after the template's start

        Ok(f.Service.Split(MediaTime.FromFrame(30 + cutAfter, f.Rate), new[] { clip.Id }));

        var parts = f.V1.Clips.OfType<TemplateClip>().OrderBy(c => c.TimelineStart).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(MediaTime.FromFrame(100, f.Rate), p.StatusAt));
        Assert.Equal(MediaTime.FromFrame(cutAfter, f.Rate), parts[1].CounterStart);
        f.AssertValid();
    }

    [Fact]
    public void Duplicate_copies_the_status_moment_and_a_trim_never_moves_it()
    {
        var f = new TimelineFixture();
        var clip = BoulderWithStatus(f, 0);
        Ok(SetStatusAt(f, clip, 120));

        Ok(f.Service.DuplicateClips(new[] { clip.Id }));
        var copy = f.V1.Clips.OfType<TemplateClip>().Single(c => c.Id != clip.Id);
        Assert.Equal(MediaTime.FromFrame(120, f.Rate), copy.StatusAt);

        // A trim of the end to before the moment: the moment stays (the status then doesn't appear).
        Ok(f.Service.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromFrame(60, f.Rate)));
        Assert.Equal(MediaTime.FromFrame(120, f.Rate), clip.StatusAt);
        f.UndoRedo.Undo();
        Ok(f.Service.TrimClip(clip.Id, ClipEdge.Start, MediaTime.FromFrame(30, f.Rate)));
        Assert.Equal(MediaTime.FromFrame(120, f.Rate), clip.StatusAt);
        f.AssertValid();
    }
}
