using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 13 Step 13.9 (D028, EX-1): the export settings as a project change — <see cref="ITimelineEditService.SetExportSettings"/>
/// (one Undo step, dirty, no step for the same settings, settings not offered refused) and
/// <see cref="ITimelineEditService.SetProjectSettings"/> with export settings: alone they go to <c>SetExportSettings</c>; with the
/// canvas and / or the rate they are part of the same single step; a refusal of any part changes nothing.
/// </summary>
public class ExportSettingsEditTests
{
    private static readonly FrameRate Fps25 = FrameRate.Fps25;
    private static readonly ExportEncoding Compact = new(ExportQuality.Compact, ExportSpeedPreset.Slow, 320);

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    /// <summary>A locked 25 fps project with a text clip moved down, at its save point.</summary>
    private static (TimelineFixture F, TextClip Title) Scene()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Fps25;
        f.Settings.IsFrameRateLocked = true;
        Ok(f.Service.AddTextClip(MediaTime.Zero));
        var title = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange { Visual = VisualProperties.Of(title)!.Value with { PositionY = 100 } }));
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);
        return (f, title);
    }

    [Fact]
    public void Export_settings_are_one_undo_step_that_makes_the_project_dirty()
    {
        var (f, _) = Scene();
        var (before, changes) = (f.Snapshot(), f.TimelineChangedCount);

        var result = f.Service.SetExportSettings(Compact);

        Ok(result);
        Assert.Same(Compact, f.Settings.Export);
        Assert.Equal("Change Export Settings", Top(f));
        Assert.Contains("320 kbps", result.Message);
        Assert.True(f.Project.IsDirty);
        Assert.Equal(before, f.Snapshot());                                // the timeline doesn't change …
        Assert.Equal(changes, f.TimelineChangedCount);                     // … and isn't notified

        f.UndoRedo.Undo();
        Assert.Same(ExportEncoding.Default, f.Settings.Export);
        Assert.False(f.Project.IsDirty);
        f.UndoRedo.Redo();
        Assert.Same(Compact, f.Settings.Export);
    }

    [Fact]
    public void The_same_settings_change_nothing()
    {
        var (f, _) = Scene();
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.SetExportSettings(new ExportEncoding(ExportQuality.High, ExportSpeedPreset.Medium, 192));

        Assert.True(result.Success && result.NoChange);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.False(f.Project.IsDirty);
    }

    [Theory]
    [InlineData(7, 1, 192)]
    [InlineData(1, 7, 192)]
    [InlineData(1, 1, 200)]
    public void Settings_that_are_not_offered_are_refused(int quality, int preset, int bitrate)
    {
        var (f, _) = Scene();
        var top = f.UndoRedo.CurrentPosition;
        var bad = new ExportEncoding((ExportQuality)quality, (ExportSpeedPreset)preset, bitrate);

        Assert.False(f.Service.SetExportSettings(bad).Success);
        Assert.False(f.Service.SetProjectSettings(1080, 1920, FrameRate.Fps30, bad).Success);   // nothing else applied either
        Assert.Same(ExportEncoding.Default, f.Settings.Export);
        Assert.Equal((1920, 1080, Fps25), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate));
        Assert.Same(top, f.UndoRedo.CurrentPosition);
    }

    [Fact]
    public void Only_the_export_settings_go_to_SetExportSettings()
    {
        var (f, _) = Scene();

        Ok(f.Service.SetProjectSettings(1920, 1080, Fps25, Compact));

        Assert.Equal("Change Export Settings", Top(f));
        Assert.Same(Compact, f.Settings.Export);
    }

    [Fact]
    public void A_kept_rate_with_the_same_export_settings_and_size_changes_nothing()
    {
        var f = new TimelineFixture();                                     // unlocked, provisional 30 fps
        var top = f.UndoRedo.CurrentPosition;

        var result = f.Service.SetProjectSettings(1920, 1080, null, ExportEncoding.Default);

        Assert.True(result.Success && result.NoChange);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.False(f.Settings.IsFrameRateLocked);
    }

    [Fact]
    public void Canvas_rate_and_export_settings_together_are_one_step_and_undo_restores_all()
    {
        var (f, title) = Scene();
        var before = f.Snapshot();
        var changes = f.TimelineChangedCount;

        var result = f.Service.SetProjectSettings(3840, 2160, FrameRate.Fps30, Compact);

        Ok(result);
        Assert.Equal("Change Project Settings", Top(f));
        Assert.Contains("3840 × 2160", result.Message);
        Assert.Contains("30 FPS", result.Message);
        Assert.Contains("Export settings", result.Message);
        Assert.Equal((3840, 2160, FrameRate.Fps30, true), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        Assert.Same(Compact, f.Settings.Export);
        Assert.Equal(200.0, title.PositionY);
        Assert.Equal(changes + 1, f.TimelineChangedCount);

        f.UndoRedo.Undo();                                                // one Undo: everything back
        Assert.Equal(before, f.Snapshot());
        Assert.Equal((1920, 1080, Fps25), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate));
        Assert.Same(ExportEncoding.Default, f.Settings.Export);
        Assert.Equal(100.0, title.PositionY);
        Assert.False(f.Project.IsDirty);

        f.UndoRedo.Redo();
        Assert.Same(Compact, f.Settings.Export);
        Assert.Equal(3840, f.Settings.FrameWidth);
    }

    [Fact]
    public void The_canvas_with_a_kept_provisional_rate_and_new_export_settings_is_one_step_and_leaves_the_rate_unlocked()
    {
        var f = new TimelineFixture();

        Ok(f.Service.SetProjectSettings(1080, 1080, null, Compact));

        Assert.Equal("Change Project Settings", Top(f));
        Assert.Equal((1080, 1080, false), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.IsFrameRateLocked));
        Assert.Same(Compact, f.Settings.Export);
    }

    [Fact]
    public void A_refused_canvas_part_keeps_the_export_settings_unchanged()
    {
        var (f, title) = Scene();
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange { Text = TextProperties.Of(title)!.Value with { FontSize = 600 } }));
        f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);
        var (top, changes) = (f.UndoRedo.CurrentPosition, f.TimelineChangedCount);

        var result = f.Service.SetProjectSettings(3840, 2160, Fps25, Compact);

        Assert.False(result.Success);
        Assert.Contains("Font size", result.Message);
        Assert.Same(ExportEncoding.Default, f.Settings.Export);
        Assert.Equal(1920, f.Settings.FrameWidth);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void Applied_export_settings_are_saved_and_reach_the_next_export_job()
    {
        var (f, _) = Scene();
        Ok(f.Service.SetExportSettings(Compact));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(f.Project, @"C:\P"), @"C:\P", _ => true);
        Assert.Equal(Compact, loaded.Settings.Export);

        var check = ExportPreflight.Check(f.Project, @"C:\Exports\out.mp4",
            new ExportPreflightEnvironment(true, _ => true, _ => true, folder => folder == @"C:\Exports"));
        Assert.True(check.CanExport, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Same(Compact, check.Job!.Encoding);
    }
}
