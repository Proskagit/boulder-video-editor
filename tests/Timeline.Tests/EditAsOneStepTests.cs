using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D058 §3: several edits as one Undo step — one Ctrl+Z undoes all, Ctrl+Y redoes all; a refused edit leaves none behind;
/// nothing changed adds no step; the same merge key in a row is one step; not nested.
/// </summary>
public sealed class EditAsOneStepTests
{
    private readonly TimelineFixture _f = new();

    private IReadOnlyList<VideoClip> Clips(int count)
    {
        for (var i = 0; i < count; i++) Assert.True(_f.Service.AddClip(_f.Video(5, _f.Rate, $"v{i}.mp4").Id).Success);
        return _f.V1.Clips.OfType<VideoClip>().ToList();
    }

    private TimelineEditResult Opacity(IEnumerable<VideoClip> clips, double opacity, string? key = null) =>
        _f.Service.EditAsOneStep("Opacity", key, () => clips.Select(c =>
            _f.Service.SetClipProperties(c.Id, new ClipPropertyChange { Visual = VisualProperties.Of(c)!.Value with { Opacity = opacity } })).ToList());

    [Fact]
    public void Several_edits_are_one_undo_step_and_one_redo_step()
    {
        var clips = Clips(3);
        var before = _f.UndoRedo.NextUndo;
        var result = Opacity(clips, 0.5);
        Assert.True(result.Success);
        Assert.Equal(clips.Select(c => c.Id).OrderBy(i => i), result.ClipIds.OrderBy(i => i));
        Assert.IsType<GroupEditCommand>(_f.UndoRedo.NextUndo);
        _f.UndoRedo.Undo();
        Assert.All(clips, c => Assert.Equal(1.0, c.Opacity));
        Assert.Same(before, _f.UndoRedo.NextUndo);
        _f.UndoRedo.Redo();
        Assert.All(clips, c => Assert.Equal(0.5, c.Opacity));
    }

    [Fact]
    public void A_refused_edit_leaves_none_of_them_and_no_step()
    {
        var clips = Clips(2);
        var before = _f.UndoRedo.NextUndo;
        var result = _f.Service.EditAsOneStep("Mixed", null, () => new[]
        {
            _f.Service.SetClipBackground(clips[0].Id, ClipBackground.Blur),
            _f.Service.SetClipProperties(clips[1].Id, new ClipPropertyChange { Visual = VisualProperties.Of(clips[1])!.Value with { Opacity = 7 } })   // out of range
        });
        Assert.False(result.Success);
        Assert.Equal(ClipBackground.Black, clips[0].Background);
        Assert.Same(before, _f.UndoRedo.NextUndo);
    }

    [Fact]
    public void Nothing_changed_is_no_step_and_the_same_key_in_a_row_merges()
    {
        var clips = Clips(2);
        var before = _f.UndoRedo.NextUndo;
        Assert.True(Opacity(clips, 1.0).NoChange);
        Assert.Same(before, _f.UndoRedo.NextUndo);

        Opacity(clips, 0.8, "opacity");
        Opacity(clips, 0.6, "opacity");                                         // a slider dragged over the group
        _f.UndoRedo.Undo();
        Assert.All(clips, c => Assert.Equal(1.0, c.Opacity));
        Assert.Same(before, _f.UndoRedo.NextUndo);
    }

    [Fact]
    public void It_is_not_nested()
    {
        Clips(1);
        Assert.Throws<InvalidOperationException>(() =>
            _f.Service.EditAsOneStep("Outer", null, () => new[] { _f.Service.EditAsOneStep("Inner", null, Array.Empty<TimelineEditResult>) }));
        Assert.True(_f.Service.EditAsOneStep("After", null, Array.Empty<TimelineEditResult>).NoChange);   // usable again
    }
}
