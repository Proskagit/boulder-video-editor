using System.Xml.Linq;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.UI.Views.Panels;
using Avalonia;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D069 §2: the empty timeline's hints — the Preview's wraps to the canvas's picture, the timeline's sits below the
/// tracks.</summary>
public sealed class Phase50RemarksTests
{
    [Fact]
    public void The_previews_empty_hint_wraps_to_the_canvas_picture()
    {
        // A vertical 1080x1920 canvas in a 900x500 Preview is drawn ≈ 281 px wide: the hint stays inside it.
        Assert.Equal(1080.0 * 500 / 1920 - 24, PreviewView.EmptyHintWidth(new Size(900, 500), new FrameSize(1080, 1920)), 6);
        // A landscape canvas keeps the old 420 px limit; a tiny area keeps a readable 120.
        Assert.Equal(420, PreviewView.EmptyHintWidth(new Size(1200, 700), new FrameSize(1920, 1080)));
        Assert.Equal(120, PreviewView.EmptyHintWidth(new Size(100, 60), new FrameSize(1080, 1920)));
        Assert.Equal(420, PreviewView.EmptyHintWidth(default, new FrameSize(1920, 1080)));
    }

    [Fact]
    public void The_timelines_empty_hint_starts_below_the_ruler_and_the_tracks()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var xaml = XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "TimelineView.axaml"));
        var hint = Assert.Single(xaml.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "EmptyTimelineHint"));
        var spacer = hint.Elements().First();
        Assert.Equal("{Binding PlayheadHeight}", (string?)spacer.Attribute("Height"));
    }

    /// <summary>D069 §6: the tool bar's former «Привязка» is the main track's magnet.</summary>
    [Fact]
    public void The_magnet_button_binds_the_main_tracks_magnet()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var xaml = XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "TimelineView.axaml"));
        var button = Assert.Single(xaml.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "MagnetButton"));
        Assert.Equal("{Binding MagneticMainTrack}", (string?)button.Attribute("IsChecked"));
        Assert.DoesNotContain(xaml.Descendants(), e => (string?)e.Attribute("IsChecked") == "{Binding SnappingEnabled}");
    }
}
