using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline.Commands;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

public class ClipPropertyEditTests
{
    // Values that don't survive decimal/float round trips or re-derivation.
    private const double Third = 1.0 / 3.0;
    private static readonly double PointThree = 0.1 + 0.2; // 0.30000000000000004

    private static TimelineFixture WithVideo(out VideoClip clip)
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Video(10, FrameRate.Ntsc30).Id).Success);
        clip = (VideoClip)f.V1.Clips[0];
        return f;
    }

    private static TextClip PlaceText(TimelineFixture f, long startFrame = 0, long endFrame = 30)
    {
        var clip = new TextClip
        {
            Text = "Title",
            TimelineStart = MediaTime.FromFrame(startFrame, f.Rate),
            Duration = MediaTime.FromFrame(endFrame, f.Rate) - MediaTime.FromFrame(startFrame, f.Rate)
        };
        f.V1.Clips.Add(clip);
        return clip;
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");

    /// <summary>Bit-exact dump of timing and every editable property of every clip.</summary>
    private static string State(TimelineFixture f)
    {
        var sb = new StringBuilder(f.Snapshot());
        foreach (var clip in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks).SelectMany(t => t.Clips))
        {
            sb.Append(clip.Id).Append(':');
            switch (clip)
            {
                case VideoClip v:
                    sb.Append(string.Join(",", Bits(v.PositionX), Bits(v.PositionY), Bits(v.Scale), Bits(v.RotationDegrees),
                        Bits(v.Opacity), Bits(v.Volume), v.IsMuted, Bits(v.Crop.Left), Bits(v.Crop.Top), Bits(v.Crop.Right), Bits(v.Crop.Bottom)));
                    break;
                case AudioClip a:
                    sb.Append(string.Join(",", Bits(a.Volume), a.IsMuted));
                    break;
                case ImageClip i:
                    sb.Append(string.Join(",", Bits(i.PositionX), Bits(i.PositionY), Bits(i.Scale), Bits(i.RotationDegrees),
                        Bits(i.Opacity), Bits(i.Crop.Left), Bits(i.Crop.Top), Bits(i.Crop.Right), Bits(i.Crop.Bottom)));
                    break;
                case TextClip t:
                    sb.Append(string.Join(",", t.Text, t.FontFamily, Bits(t.FontSize), t.ColorHex, t.Alignment,
                        Bits(t.PositionX), Bits(t.PositionY), Bits(t.Scale), Bits(t.RotationDegrees), Bits(t.Opacity)));
                    break;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static VisualProperties Visual(Clip clip) => VisualProperties.Of(clip)!.Value;

    private static TimelineEditResult SetVisual(TimelineFixture f, Clip clip, Func<VisualProperties, VisualProperties> edit) =>
        f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = edit(Visual(clip)) });

    private static TimelineEditResult SetOpacity(TimelineFixture f, Clip clip, double opacity) =>
        SetVisual(f, clip, v => v with { Opacity = opacity });

    private static int UndoSteps(TimelineFixture f)
    {
        var steps = 0;
        while (f.UndoRedo.CanUndo) { f.UndoRedo.Undo(); steps++; }
        for (var i = 0; i < steps; i++) f.UndoRedo.Redo();
        return steps;
    }

    // --- Applying and undoing -------------------------------------------------------

    [Fact]
    public void Visual_IsStoredExactly_AndUndoRedoRestoreEveryBit()
    {
        var f = WithVideo(out var clip);
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);
        var before = State(f);
        var edits = f.TimelineChangedCount;

        var visual = new VisualProperties(-Third, 1e-7 + 123.456, PointThree, -359.999999999, Third,
            new CropRect(PointThree, Third, 0.1, 1e-12));
        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual });

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { clip.Id }, result.ClipIds);
        Assert.Equal(visual, Visual(clip));
        Assert.Equal(Bits(Third), Bits(clip.Opacity));
        Assert.Equal(edits + 1, f.TimelineChangedCount);
        Assert.True(f.Project.IsDirty);
        var after = State(f);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        Assert.False(f.Project.IsDirty);

        f.UndoRedo.Redo();
        Assert.Equal(after, State(f));
        f.AssertValid();
    }

    [Fact]
    public void Editing_Properties_NeverChangesTiming()
    {
        var f = WithVideo(out var clip);
        var timing = f.Snapshot();

        Assert.True(SetVisual(f, clip, v => v with { Scale = 2, PositionX = 10 }).Success);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(0.5, true) }).Success);

        Assert.Equal(timing, f.Snapshot());
    }

    [Fact]
    public void Audio_OnVideoAndAudioClips_MuteIsSeparateFromVolume()
    {
        var f = WithVideo(out var video);
        Assert.True(f.Service.AddClip(f.Audio(5).Id).Success);
        var audio = (AudioClip)f.A1.Clips[0];

        Assert.True(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Audio = new AudioProperties(1.5, false) }).Success);
        Assert.True(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Audio = new AudioProperties(1.5, true) }).Success);
        Assert.True(f.Service.SetClipProperties(audio.Id, new ClipPropertyChange { Audio = new AudioProperties(PointThree, true) }).Success);

        Assert.Equal((1.5, true), (video.Volume, video.IsMuted));
        Assert.Equal((PointThree, true), (audio.Volume, audio.IsMuted));

        f.UndoRedo.Undo(); // audio clip
        f.UndoRedo.Undo(); // mute only: the volume stays
        Assert.Equal((1.5, false), (video.Volume, video.IsMuted));
    }

    [Fact]
    public void Text_IsStoredExactly_IncludingSeveralLines()
    {
        var f = new TimelineFixture();
        var clip = PlaceText(f);
        var before = State(f);

        var text = new TextProperties("First line\r\nSecond line\nThird  ", "Arial", 72.5, "#ff8800", TextAlignment.Right);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = text }).Success);
        Assert.Equal(text, TextProperties.Of(clip));

        Assert.True(SetVisual(f, clip, v => v with { Opacity = 0.25, RotationDegrees = 90 }).Success);
        Assert.Equal((0.25, 90.0), (clip.Opacity, clip.RotationDegrees));

        f.UndoRedo.Undo();
        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
    }

    [Fact]
    public void EmptyText_IsAllowed()
    {
        var f = new TimelineFixture();
        var clip = PlaceText(f);
        var text = TextProperties.Of(clip)!.Value with { Text = "" };

        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = text }).Success);
        Assert.Equal("", clip.Text);
    }

    [Fact]
    public void Image_Visual_IncludingCrop()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        var image = (ImageClip)f.V1.Clips[0];

        Assert.True(SetVisual(f, image, v => v with { Crop = new CropRect(0.5, 0, 0.49, 0.999) }).Success);
        Assert.Equal(new CropRect(0.5, 0, 0.49, 0.999), image.Crop);
    }

    [Fact]
    public void SeveralGroupsInOneChange_AreOneStep()
    {
        var f = WithVideo(out var clip);
        var before = State(f);

        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = Visual(clip) with { Opacity = 0.5 },
            Audio = new AudioProperties(2, true)
        });

        Assert.True(result.Success);
        Assert.Equal(2, UndoSteps(f)); // Add + one property step
        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
    }

    [Fact]
    public void SameValues_AreNoChange_WithoutUndoStep()
    {
        var f = WithVideo(out var clip);
        var edits = f.TimelineChangedCount;

        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = Visual(clip), Audio = AudioProperties.Of(clip) });

        Assert.True(result.Success);
        Assert.True(result.NoChange);
        Assert.Equal(edits, f.TimelineChangedCount);
        Assert.Equal(1, UndoSteps(f)); // only the Add
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange()).NoChange);
    }

    // --- Rejections: nothing changes ------------------------------------------------

    public static TheoryData<string, VisualProperties> InvalidVisuals()
    {
        var ok = new VisualProperties(0, 0, 1, 0, 1, CropRect.None);
        return new TheoryData<string, VisualProperties>
        {
            { "opacity < 0", ok with { Opacity = -0.0001 } },
            { "opacity > 1", ok with { Opacity = 1.0000001 } },
            { "opacity NaN", ok with { Opacity = double.NaN } },
            { "scale 0", ok with { Scale = 0 } },
            { "scale below min", ok with { Scale = 0.0099 } },
            { "scale above max", ok with { Scale = 10.01 } },
            { "scale infinite", ok with { Scale = double.PositiveInfinity } },
            { "rotation above max", ok with { RotationDegrees = 360.5 } },
            { "rotation below min", ok with { RotationDegrees = -361 } },
            { "position x too far", ok with { PositionX = 100_001 } },
            { "position y NaN", ok with { PositionY = double.NaN } },
            { "crop negative", ok with { Crop = new CropRect(-0.1, 0, 0, 0) } },
            { "crop edge 1", ok with { Crop = new CropRect(0, 1, 0, 0) } },
            { "crop left+right 1", ok with { Crop = new CropRect(0.5, 0, 0.5, 0) } },
            { "crop top+bottom > 1", ok with { Crop = new CropRect(0, 0.7, 0, 0.4) } },
            { "crop NaN", ok with { Crop = new CropRect(0, 0, double.NaN, 0) } },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidVisuals))]
    public void InvalidVisual_IsRejected_WithoutChanges(string _, VisualProperties visual)
    {
        var f = WithVideo(out var clip);
        var before = State(f);

        var result = f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Visual = visual });

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Equal(before, State(f));
        Assert.Equal(1, UndoSteps(f));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.0000001)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidVolume_IsRejected_WithoutChanges(double volume)
    {
        var f = WithVideo(out var clip);
        var before = State(f);

        Assert.False(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(volume, false) }).Success);
        Assert.Equal(before, State(f));
    }

    public static TheoryData<string, TextProperties> InvalidTexts()
    {
        var ok = new TextProperties("Hi", "Segoe UI", 48, "#FFFFFF", TextAlignment.Center);
        return new TheoryData<string, TextProperties>
        {
            { "null text", ok with { Text = null! } },
            { "text too long", ok with { Text = new string('x', ClipPropertyLimits.MaxTextLength + 1) } },
            { "no font", ok with { FontFamily = " " } },
            { "null font", ok with { FontFamily = null! } },
            { "font too long", ok with { FontFamily = new string('f', ClipPropertyLimits.MaxFontFamilyLength + 1) } },
            { "font size 0", ok with { FontSize = 0 } },
            { "font size too big", ok with { FontSize = 1000.5 } },
            { "font size NaN", ok with { FontSize = double.NaN } },
            { "color without #", ok with { ColorHex = "FFFFFF" } },
            { "color short", ok with { ColorHex = "#FFF" } },
            { "color with alpha", ok with { ColorHex = "#FFFFFFFF" } },
            { "color not hex", ok with { ColorHex = "#GG0000" } },
            { "color null", ok with { ColorHex = null! } },
            { "alignment", ok with { Alignment = (TextAlignment)42 } },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidTexts))]
    public void InvalidText_IsRejected_WithoutChanges(string _, TextProperties text)
    {
        var f = new TimelineFixture();
        var clip = PlaceText(f);
        var before = State(f);

        Assert.False(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = text }).Success);
        Assert.Equal(before, State(f));
        Assert.False(f.UndoRedo.CanUndo);
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        var f = WithVideo(out var clip);

        Assert.True(SetVisual(f, clip, _ => new VisualProperties(-100_000, 100_000, 0.01, -360, 0, new CropRect(0, 0.999999, 0, 0))).Success);
        Assert.True(SetVisual(f, clip, _ => new VisualProperties(0, 0, 10, 360, 1, CropRect.None)).Success);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(0, false) }).Success);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Audio = new AudioProperties(2, false) }).Success);
    }

    [Fact]
    public void GroupsThatDontApplyToTheClip_AreRejected()
    {
        var f = WithVideo(out var video);
        Assert.True(f.Service.AddClip(f.Audio(5).Id).Success);
        var audio = f.A1.Clips[0];
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        var image = f.V1.Clips[1];
        var text = PlaceText(f, 600, 630);
        var before = State(f);
        var anyVisual = new VisualProperties(0, 0, 1, 0, 1, CropRect.None);
        var anyText = new TextProperties("x", "Arial", 10, "#000000", TextAlignment.Left);

        Assert.False(f.Service.SetClipProperties(audio.Id, new ClipPropertyChange { Visual = anyVisual }).Success);
        Assert.False(f.Service.SetClipProperties(image.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }).Success);
        Assert.False(f.Service.SetClipProperties(text.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }).Success);
        Assert.False(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Text = anyText }).Success);
        Assert.False(f.Service.SetClipProperties(text.Id, new ClipPropertyChange { Visual = anyVisual with { Crop = new CropRect(0.1, 0, 0, 0) } }).Success);
        // A valid group together with an invalid one: nothing at all is applied.
        Assert.False(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Visual = anyVisual with { Opacity = 0.5 }, Text = anyText }).Success);

        Assert.Equal(before, State(f));
    }

    [Fact]
    public void LockedTrack_IsRejected()
    {
        var f = WithVideo(out var clip);
        f.V1.IsLocked = true;
        var before = State(f);

        var result = SetOpacity(f, clip, 0.5);

        Assert.False(result.Success);
        Assert.Contains("locked", result.Message);
        Assert.Equal(before, State(f));
    }

    [Fact]
    public void UnknownClip_IsRejected()
    {
        var f = WithVideo(out _);
        Assert.False(f.Service.SetClipProperties(Guid.NewGuid(), new ClipPropertyChange { Audio = new AudioProperties(1, true) }).Success);
    }

    // --- Merging into one undo step --------------------------------------------------

    [Fact]
    public void ConsecutiveChangesOfOneProperty_MergeIntoOneStep()
    {
        var f = WithVideo(out var clip);
        var before = State(f);

        foreach (var opacity in new[] { 0.95, 0.9, 0.85, PointThree })
            Assert.True(SetOpacity(f, clip, opacity).Success);

        Assert.Equal(2, UndoSteps(f)); // Add + one opacity step
        var top = Assert.IsType<SetClipPropertiesCommand>(((NotifyingCommand)f.UndoRedo.CurrentPosition).Inner);
        Assert.Equal("Change Opacity", top.Description);
        Assert.Equal(PointThree, clip.Opacity);

        f.UndoRedo.Undo();
        Assert.Equal(before, State(f));
        f.UndoRedo.Redo();
        Assert.Equal(Bits(PointThree), Bits(clip.Opacity));
    }

    [Fact]
    public void TypingText_MergesIntoOneStep()
    {
        var f = new TimelineFixture();
        var clip = PlaceText(f);
        var original = clip.Text;

        foreach (var text in new[] { "H", "He", "Hel", "Hel\nlo" })
            Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange { Text = TextProperties.Of(clip)!.Value with { Text = text } }).Success);

        Assert.Equal(1, UndoSteps(f));
        f.UndoRedo.Undo();
        Assert.Equal(original, clip.Text);
    }

    [Fact]
    public void DifferentProperty_OrDifferentClip_StartsANewStep()
    {
        var f = WithVideo(out var a);
        Assert.True(f.Service.AddClip(f.Image().Id).Success);
        var b = f.V1.Clips[1];

        Assert.True(SetOpacity(f, a, 0.5).Success);
        Assert.True(SetOpacity(f, a, 0.4).Success);     // merged
        Assert.True(SetVisual(f, a, v => v with { Scale = 2 }).Success);
        Assert.True(SetOpacity(f, a, 0.3).Success);     // opacity again, but not consecutive
        Assert.True(SetOpacity(f, b, 0.3).Success);     // other clip
        Assert.True(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Audio = new AudioProperties(1, true) }).Success);
        Assert.True(f.Service.SetClipProperties(a.Id, new ClipPropertyChange { Audio = new AudioProperties(0.5, true) }).Success); // volume ≠ mute

        Assert.Equal(2 + 6, UndoSteps(f)); // two adds + six property steps
    }

    [Fact]
    public void AnInterveningTimelineEdit_StartsANewStep()
    {
        var f = WithVideo(out var clip);

        Assert.True(SetOpacity(f, clip, 0.5).Success);
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 3).Success);
        Assert.True(SetOpacity(f, clip, 0.4).Success);

        Assert.Equal(4, UndoSteps(f));
    }

    [Fact]
    public void SavedStep_IsNotMergedInto_AndUndoReturnsToClean()
    {
        var f = WithVideo(out var clip);
        Assert.True(SetOpacity(f, clip, 0.5).Success);
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition); // saved at 0.5
        Assert.False(f.Project.IsDirty);

        Assert.True(SetOpacity(f, clip, 0.4).Success);
        Assert.True(SetOpacity(f, clip, 0.3).Success);
        Assert.True(f.Project.IsDirty);
        Assert.Equal(3, UndoSteps(f));

        f.UndoRedo.Undo();
        Assert.Equal(0.5, clip.Opacity);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void ChangingBackToTheSavedValue_RemovesTheStep_AndIsClean()
    {
        var f = WithVideo(out var clip);
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);

        Assert.True(SetOpacity(f, clip, 0.9).Success);
        Assert.True(f.Project.IsDirty);
        Assert.True(SetOpacity(f, clip, 1.0).Success);

        Assert.False(f.Project.IsDirty);
        Assert.Equal(1, UndoSteps(f)); // only the Add remains
    }

    [Fact]
    public void AfterUndo_ANewChangeIsItsOwnStep()
    {
        var f = WithVideo(out var clip);
        Assert.True(SetOpacity(f, clip, 0.5).Success);
        Assert.True(SetVisual(f, clip, v => v with { Scale = 2 }).Success);
        f.UndoRedo.Undo(); // scale back to 1; the top is the opacity step

        Assert.True(SetOpacity(f, clip, 0.4).Success);

        Assert.Equal(3, UndoSteps(f));
        f.UndoRedo.Undo();
        Assert.Equal(0.5, clip.Opacity);
    }

    // --- Interaction with other edits ----------------------------------------------

    [Fact]
    public void Split_CopiesAllProperties_IncludingVideoMute()
    {
        var f = WithVideo(out var clip);
        Assert.True(f.Service.SetClipProperties(clip.Id, new ClipPropertyChange
        {
            Visual = new VisualProperties(Third, -5, 1.5, 45, 0.5, new CropRect(0.1, 0.2, 0.3, 0.4)),
            Audio = new AudioProperties(PointThree, true)
        }).Success);

        var result = f.Service.Split(MediaTime.FromFrame(100, f.Rate));

        Assert.True(result.Success);
        var right = (VideoClip)f.V1.Clips[1];
        Assert.Equal(VisualProperties.Of(clip), VisualProperties.Of(right));
        Assert.Equal(AudioProperties.Of(clip), AudioProperties.Of(right));
    }

    [Fact]
    public void UndoOfAPropertyStep_AfterLaterTimelineEdits_RestoresOnlyThatStep()
    {
        var f = WithVideo(out var clip);
        Assert.True(SetOpacity(f, clip, 0.25).Success);
        var afterOpacity = State(f);
        Assert.True(f.Service.MoveClips(TimelineFixture.Ids(clip), 10).Success);

        f.UndoRedo.Undo(); // move
        Assert.Equal(afterOpacity, State(f));
        f.UndoRedo.Undo(); // opacity
        Assert.Equal(1.0, clip.Opacity);
        f.AssertValid();
    }
}
