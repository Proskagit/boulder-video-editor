using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// The text rule of every Inspector numeric field (Volume, Position X/Y, Scale, Rotation, Opacity,
/// Crop): only a leading sign, digits and the culture's decimal separator. Avalonia's default
/// (<see cref="NumberStyles.Any"/>) turned "9 0" into 90 under ru-RU. An emptied field changes
/// nothing and shows the model's value again.
/// </summary>
public sealed class NumericInputTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static bool TryParse(string text, CultureInfo culture, out decimal value) =>
        decimal.TryParse(text, NumericInput.ParsingStyle, culture, out value);

    [Theory]
    [InlineData("90", 90)]
    [InlineData("0", 0)]
    [InlineData("-45", -45)]
    [InlineData("12,5", 12.5)]
    [InlineData("-0,25", -0.25)]
    [InlineData("200", 200)]
    public void Plain_numbers_parse_as_before(string text, double expected)
    {
        Assert.True(TryParse(text, Ru, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void The_decimal_separator_follows_the_culture()
    {
        Assert.True(TryParse("12.5", En, out var en));
        Assert.Equal(12.5m, en);
        Assert.False(TryParse("12.5", Ru, out _)); // not a separator in ru-RU, as before
    }

    [Theory]
    [InlineData("9 0")]          // ru-RU group separator: was 90
    [InlineData("1 000")]
    [InlineData("1 000")]   // the real ru-RU group separator (NBSP)
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("\t5")]
    [InlineData("(5)")]          // parentheses: was -5
    [InlineData("5-")]           // trailing sign: was -5
    [InlineData("1e2")]          // exponent: was 100
    [InlineData("5₽")]           // currency: was 5
    [InlineData("5a")]
    [InlineData("--5")]
    [InlineData("5,5,5")]
    [InlineData("%")]
    public void Whitespace_and_foreign_characters_are_not_a_value(string text)
    {
        Assert.False(TryParse(text, Ru, out _));
        Assert.False(TryParse(text, En, out _));
    }

    [Fact]
    public void The_old_default_accepted_the_reported_input()
    {
        // Documents the defect this rule fixes (Avalonia's NumericUpDown default).
        Assert.True(decimal.TryParse("9 0", NumberStyles.Any, Ru, out var value));
        Assert.Equal(90m, value);
    }

    // --- Empty field --------------------------------------------------------------------------------

    [Fact]
    public void An_emptied_field_changes_nothing_and_shows_the_model_again_on_blur()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());

        var clip = new VideoClip { MediaAssetId = Guid.NewGuid(), TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(25, FrameRate.Fps25), Scale = 0.5, Volume = 1.5 };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "v.mp4", null, FrameRate.Fps25));
        var timelineChanges = 0;
        projects.TimelineChanged += (_, _) => timelineChanges++;

        // What NumericUpDown sends for an empty field.
        inspector.ScalePercent = null;
        inspector.VolumePercent = null;
        inspector.CropLeftPercent = null;
        inspector.PositionX = null;

        Assert.Equal(0.5, clip.Scale);
        Assert.Equal(1.5, clip.Volume);
        Assert.Equal(0, timelineChanges);
        Assert.False(undo.CanUndo);

        var raised = new List<string?>();
        inspector.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        inspector.ShowModelValues();                     // the field lost focus

        Assert.Equal(50m, inspector.ScalePercent);
        Assert.Equal(150m, inspector.VolumePercent);
        Assert.Equal(0m, inspector.CropLeftPercent);
        Assert.Equal(0m, inspector.PositionX);
        Assert.Contains(nameof(InspectorViewModel.ScalePercent), raised); // the view is told
        Assert.Equal(0, timelineChanges);                // showing is not an edit
        Assert.False(undo.CanUndo);

        inspector.ScalePercent = 80;                     // editing works as before
        Assert.Equal(0.8, clip.Scale, 10);
        Assert.True(undo.CanUndo);
    }
}
