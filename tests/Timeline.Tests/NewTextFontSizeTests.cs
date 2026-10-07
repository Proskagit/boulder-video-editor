using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 15 Step 15.8 (D029 §3 → D030 §9, Q14): a new text clip takes <c>FontSize = 48 × canvasHeight / 1080</c>, exact
/// in <c>double</c>. A canvas change keeps D028's rule for existing text — the font size times
/// <c>min(W'/W, H'/H)</c>, its Scale untouched — so a 1920 × 1080 title of 48 becomes 27 on 1080 × 1920 while a new
/// title there gets 85.333…: two different operations. Copies keep their source's size; the value saves and opens as is.
/// </summary>
public class NewTextFontSizeTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    private static TextClip NewText(TimelineFixture f, long frame = 0)
    {
        var result = f.Service.AddTextClip(MediaTime.FromFrame(frame, f.Rate));
        Ok(result);
        return (TextClip)f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds[0]);
    }

    [Theory]
    [InlineData(1280, 720, 32.0)]
    [InlineData(1920, 1080, 48.0)]
    [InlineData(2560, 1440, 64.0)]
    [InlineData(3840, 2160, 96.0)]
    [InlineData(640, 360, 16.0)]
    public void A_new_text_takes_48_times_the_canvas_height_over_1080(int width, int height, double expected)
    {
        var f = new TimelineFixture();
        if ((width, height) != (f.Settings.FrameWidth, f.Settings.FrameHeight)) Ok(f.Service.SetCanvasSize(width, height));

        Assert.Equal(expected, NewText(f).FontSize);
    }

    [Fact]
    public void A_new_text_on_a_portrait_canvas_is_85_333_exactly_unrounded()
    {
        var f = new TimelineFixture();
        Ok(f.Service.SetCanvasSize(1080, 1920));

        var size = NewText(f).FontSize;

        Assert.Equal(48.0 * 1920 / 1080, size);                                       // 85.333…, no rounding
        Assert.Equal(85.333, size, 3);
        Assert.Equal(48.0 * 1920 / 1080, ProjectSettingsRules.NewTextFontSize(1920));
    }

    [Theory]
    [InlineData(1000, 810, 36.0)]                                                     // custom sizes, not presets
    [InlineData(2000, 1530, 68.0)]
    [InlineData(1660, 2970, 132.0)]
    public void A_new_text_on_a_custom_canvas_takes_the_same_formula(int width, int height, double expected)
    {
        var f = new TimelineFixture();
        Ok(f.Service.SetCanvasSize(width, height));

        Assert.Equal(expected, NewText(f).FontSize);
    }

    [Fact]
    public void A_new_text_on_a_custom_canvas_keeps_a_non_integer_size_unrounded()
    {
        var f = new TimelineFixture();
        Ok(f.Service.SetCanvasSize(1234, 1000));                                     // custom; 48 000 / 1080 = 44.444…

        var size = NewText(f).FontSize;

        Assert.Equal(44.444444444444, size, 12);
        Assert.NotEqual(Math.Round(size), size);
    }

    [Theory]
    [InlineData(128, ProjectSettingsRules.MinCanvasSide, 2.844444444444)]            // the lowest canvas the rules accept
    [InlineData(2304, ProjectSettingsRules.MaxCanvasSide, 182.044444444444)]         // the highest (2304 × 4096 within the area)
    public void At_the_canvas_limits_a_new_text_is_within_the_font_size_limits_without_a_clamp(int width, int height, double expected)
    {
        Assert.Null(ProjectSettingsRules.CanvasError(width, height));
        var f = new TimelineFixture();
        Ok(f.Service.SetCanvasSize(width, height));

        var text = NewText(f);

        Assert.Equal(expected, text.FontSize, 9);                                      // the formula's value, not a limit
        Assert.InRange(text.FontSize, ClipPropertyLimits.MinFontSize, ClipPropertyLimits.MaxFontSize);
        Assert.Null(ClipPropertyValidator.ValidateCurrent(text));                      // the ordinary property rules accept it
    }

    [Fact]
    public void An_existing_text_keeps_D028s_rule_on_a_canvas_change_and_its_scale_while_a_new_one_uses_the_height()
    {
        var f = new TimelineFixture();                                                 // 1920 × 1080
        var title = NewText(f);
        Assert.Equal(48.0, title.FontSize);
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange
        {
            Visual = VisualProperties.Of(title)!.Value with { Scale = 1.5, PositionX = 200, PositionY = -100, RotationDegrees = 10, Opacity = 0.8 }
        }));

        Ok(f.Service.SetCanvasSize(1080, 1920));                                       // s = min(1080/1920, 1920/1080) = 0.5625

        Assert.Equal(27.0, title.FontSize);                                             // 48 × 0.5625 (D028 Variant B)
        Assert.Equal((1.5, 10.0, 0.8), (title.Scale, title.RotationDegrees, title.Opacity));   // Scale, rotation, opacity kept
        Assert.Equal((112.5, -56.25), (title.PositionX, title.PositionY));             // positions × s, as before
        Assert.Equal(48.0 * 1920 / 1080, NewText(f, 300).FontSize);                     // a new title on the same canvas

        f.UndoRedo.Undo();                                                             // the new title
        f.UndoRedo.Undo();                                                             // the canvas change
        Assert.Equal((48.0, 1.5), (title.FontSize, title.Scale));
    }

    [Fact]
    public void Copies_keep_their_sources_size_on_any_canvas()
    {
        var f = new TimelineFixture();
        var title = NewText(f);                                                        // 48
        Ok(f.Service.SetCanvasSize(3840, 2160));                                       // the title × 2 → 96
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange { Text = TextProperties.Of(title)!.Value with { FontSize = 70 } }));

        var duplicate = f.Service.DuplicateClips(TimelineFixture.Ids(title));
        Ok(duplicate);

        var copy = (TextClip)f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Single(c => c.Id == duplicate.ClipIds[0]);
        Assert.Equal(70.0, copy.FontSize);                                              // not the new-text formula
    }

    [Fact]
    public async Task The_size_saves_and_opens_exactly_in_format_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            Ok(f.Service.SetCanvasSize(1080, 1920));
            var title = NewText(f);
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            var file = Path.Combine(folder, "project.json");
            Assert.Contains("\"formatVersion\": 3", File.ReadAllText(file));
            var bytes = File.ReadAllBytes(file);

            var reopened = await f.Projects.OpenAsync(folder);

            var again = reopened.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single(c => c.Id == title.Id);
            Assert.Equal(48.0 * 1920 / 1080, again.FontSize);
            await f.Projects.SaveAsync();
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Adding_a_text_changes_the_saved_file_only_by_the_new_clip()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var f = new TimelineFixture();
            Ok(f.Service.SetCanvasSize(1280, 720));
            var video = f.Video(10, f.Rate);
            f.Place<VideoClip>(f.V1, video, 0, MediaTime.FromSeconds(4).Ticks);
            f.Place<AudioClip>(f.A1, f.Audio(10), 0, MediaTime.FromSeconds(6).Ticks);
            var existing = NewText(f, MediaTime.FromSeconds(5).ToNearestFrame(f.Rate));     // 32 on 720
            f.AssertValid();
            var folder = Path.Combine(root, "Project");
            await f.Projects.SaveAsAsync(folder);
            var file = Path.Combine(folder, "project.json");
            var before = JsonNode.Parse(File.ReadAllText(file))!;

            var added = NewText(f, MediaTime.FromSeconds(12).ToNearestFrame(f.Rate));
            await f.Projects.SaveAsync();
            var after = JsonNode.Parse(File.ReadAllText(file))!;

            Assert.Equal(3, (int)before["formatVersion"]!);
            Assert.Equal(3, (int)after["formatVersion"]!);
            var clips = after["timeline"]!["videoTracks"]![0]!["clips"]!.AsArray();
            var node = clips.Single(c => (string)c!["id"]! == added.Id.ToString());
            Assert.Equal("text", (string)node!["type"]!);
            Assert.Equal(32.0, (double)node["fontSize"]!);
            Assert.Equal(32.0, (double)clips.Single(c => (string)c!["id"]! == existing.Id.ToString())!["fontSize"]!);

            // Without the new clip and the save time stamp, the file is the baseline exactly.
            clips.Remove(node);
            after.AsObject().Remove("modifiedAt");
            before.AsObject().Remove("modifiedAt");
            Assert.True(JsonNode.DeepEquals(before, after), after.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
