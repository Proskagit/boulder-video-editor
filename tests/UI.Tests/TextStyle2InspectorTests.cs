using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D075 amendment 1: the Inspector's italic, line spacing, width and shadow edit the text (a switch one step, a value
/// merging); the saved styles — saved under a name (global), applied to another text (its look only), deleted.</summary>
public sealed class TextStyle2InspectorTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly InMemoryUserSettings _settings = new();

    public TextStyle2InspectorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private TextClip Add(string words, double start)
    {
        var clip = new TextClip { Text = words, TimelineStart = MediaTime.FromSeconds(start), Duration = MediaTime.FromSeconds(5) };
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        return clip;
    }

    private InspectorViewModel Show(TextClip clip, InspectorViewModel? inspector = null)
    {
        inspector ??= new InspectorViewModel(_edit, _status, settings: _settings);
        inspector.ShowClip(new TimelineClipSelection(clip, clip.Text, null, Rate));
        return inspector;
    }

    [Fact]
    public void The_fields_edit_the_text()
    {
        var clip = Add("Трасса", 0);
        var inspector = Show(clip);
        inspector.IsItalic = true;
        inspector.LineSpacing = 1.5m;
        inspector.BoxWidthText = "640";
        inspector.IsShadowOn = true;
        inspector.ShadowBlurPercent = 20;
        inspector.ShadowOffsetXPercent = -10;
        inspector.SetShadowColorCommand.Execute("#E53935");
        Assert.True(clip.Italic);
        Assert.Equal((1.5, (double?)640), (clip.LineSpacing, clip.BoxWidth));
        Assert.Equal(new TextShadow(true, "#E53935", TextShadow.DefaultOpacity, 0.2, -0.1, TextShadow.DefaultOffset), clip.Shadow);

        inspector.BoxWidthText = "";                                       // empty: no wrapping
        Assert.Null(clip.BoxWidth);
        inspector.LineSpacing = 9m;                                        // out of range: refused, shown back
        Assert.Equal(1.5, clip.LineSpacing);
        Assert.Equal(1.5m, inspector.LineSpacing);
    }

    [Fact]
    public void A_saved_style_is_global_applies_the_look_only_and_can_be_deleted()
    {
        var source = Add("Источник", 0);
        source.FontSize = 72;
        source.Bold = source.Italic = true;
        source.Shadow = TextShadow.Default with { IsEnabled = true };
        source.BoxWidth = 500;
        var inspector = Show(source);
        inspector.NewTextStyleName = "Заголовок";
        inspector.SaveTextStyleCommand.Execute(null);
        var saved = Assert.Single(_settings.Current.TextStyles);
        Assert.Equal("Заголовок", saved.Name);
        Assert.Single(inspector.SavedTextStyles);

        var target = Add("Другой текст", 10);
        target.PositionX = 123;
        Show(target, inspector);
        inspector.ApplyTextStyleCommand.Execute(saved);
        Assert.Equal((72.0, true, true, true), (target.FontSize, target.Bold, target.Italic, target.Shadow.IsEnabled));
        Assert.Equal(("Другой текст", 123.0, (double?)null), (target.Text, target.PositionX, target.BoxWidth));   // words, place, width stay
        _undo.Undo();                                                      // one step
        Assert.False(target.Bold);

        var other = new InspectorViewModel(_edit, _status, settings: _settings);   // another panel / project: the same styles
        Assert.Single(other.SavedTextStyles);
        inspector.DeleteTextStyleCommand.Execute(saved);
        Assert.Empty(_settings.Current.TextStyles);
        Assert.Empty(other.SavedTextStyles);
    }
}
