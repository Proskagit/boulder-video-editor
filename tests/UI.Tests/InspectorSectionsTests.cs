using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D057 §3.1: the Inspector in collapsible sections — stable ids, one state for the Inspector kept across clips and in the
/// user's layout (so across restarts), the FIT service section collapsed by default, a summary in a collapsed header, no
/// tabs; the scroll position per kind of item.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class InspectorSectionsTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly IUiStateService _previous = InspectorSectionStates.Store;
    private readonly InMemoryUiState _store = new();
    private readonly InspectorViewModel _inspector;
    private readonly ProjectService _projects;

    public InspectorSectionsTests()
    {
        InspectorSectionStates.Store = _store;
        var undo = new UndoRedoService();
        _projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        var edit = new TimelineEditService(_projects, undo, NullLogger<TimelineEditService>.Instance);
        _inspector = new InspectorViewModel(edit, new StatusService());
    }

    public void Dispose() => InspectorSectionStates.Store = _previous;

    private void Show(Clip clip)
    {
        if (!_projects.Current.Timeline.VideoTracks[0].Clips.Contains(clip)) _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        var canvas = new FrameSize(_projects.Current.Settings.FrameWidth, _projects.Current.Settings.FrameHeight);
        _inspector.ShowClip(new TimelineClipSelection(clip, "C", null, Rate, Canvas: canvas));
    }

    private static TextClip Text(long start) => new() { Text = "Hi", TimelineStart = MediaTime.FromFrame(start, Rate), Duration = MediaTime.FromFrame(50, Rate) };

    private static InspectorSection Section(InspectorView view, string id) =>
        view.GetLogicalDescendants().OfType<InspectorSection>().Single(s => s.SectionId == id);

    [Fact]
    public void The_inspector_has_its_sections_by_stable_ids_and_no_tabs()
    {
        var view = new InspectorView { DataContext = _inspector };
        var ids = view.GetLogicalDescendants().OfType<InspectorSection>().Select(s => s.SectionId).ToList();
        Assert.Equal(new[] { "media", "media.tech", "template", "text", "text.animation", "transform", "crop", "holds", "fades", "speed",
            "training", "fit", "audio", "clip" }, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Empty(view.GetLogicalDescendants().OfType<TabControl>());
    }

    [Fact]
    public void A_collapsed_section_stays_collapsed_for_every_clip_and_in_a_new_inspector()
    {
        var view = new InspectorView { DataContext = _inspector };
        Show(Text(0));
        Section(view, "transform").IsExpanded = false;
        Assert.True(_store.Current.InspectorSections["transform"]);

        Show(Text(100));                                                   // another clip: the Inspector's state
        Assert.False(Section(view, "transform").IsExpanded);
        Assert.True(Section(view, "text").IsExpanded);

        var next = new InspectorView { DataContext = _inspector };         // a restart reads the user's layout
        Assert.False(Section(next, "transform").IsExpanded);
        Section(next, "transform").IsExpanded = true;
        Assert.False(_store.Current.InspectorSections["transform"]);
    }

    [Fact]
    public void The_fit_service_section_is_collapsed_until_the_user_opens_it()
    {
        var view = new InspectorView { DataContext = _inspector };
        Assert.False(Section(view, "fit").IsExpanded);
        Assert.True(Section(view, "holds").IsExpanded);
        Section(view, "fit").IsExpanded = true;
        Assert.True(Section(new InspectorView { DataContext = _inspector }, "fit").IsExpanded);
    }

    [Fact]
    public void A_collapsed_header_shows_its_summary()
    {
        var section = new InspectorSection { SectionId = "speed", Title = "SPEED", Summary = "1.5×" };
        var header = (DockPanel)section.Header!;
        var summary = header.Children.OfType<TextBlock>().Last();
        Assert.Null(summary.Text);                                         // open: the fields tell it
        section.IsExpanded = false;
        Assert.Equal("· 1.5×", summary.Text);
        section.Summary = null;
        Assert.Null(summary.Text);
    }

    [Fact]
    public void The_scroll_position_is_kept_per_kind_of_item()
    {
        var memory = new InspectorScrollMemory(_store);
        Assert.Equal(0, memory.Show("VideoClip"));
        memory.Restored();
        memory.Scrolled(420);
        Assert.Equal(0, memory.Show("TextClip"));                         // another kind: its own place
        memory.Scrolled(10);                                               // the new content's layout: not the user's
        memory.Restored();
        memory.Scrolled(80);
        Assert.Equal(420, memory.Show("VideoClip"));
        memory.Restored();
        Assert.Equal(80, memory.Show("TextClip"));
        Assert.Equal(420, _store.Current.InspectorScroll["VideoClip"]);
    }

    [Fact]
    public void The_inspector_tells_the_kind_it_shows()
    {
        Show(Text(0));
        Assert.Equal("TextClip", _inspector.ScrollKey);
        var shown = 0;
        _inspector.Shown += (_, _) => shown++;
        Show(Text(100));
        _inspector.ClearSelection();
        Assert.Equal(2, shown);
        Assert.Equal("none", _inspector.ScrollKey);
    }
}
