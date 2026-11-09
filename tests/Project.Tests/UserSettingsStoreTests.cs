using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D057 §2: the per-user files of the global settings and the working layout — the defaults without a file, a round trip,
/// a damaged file set aside, a newer one never overwritten; the settings' bounds; a burst of layout changes written once.
/// </summary>
public sealed class UserSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-user-files", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string SettingsPath => Path.Combine(_folder, "settings.json");
    private string UiPath => Path.Combine(_folder, "ui-state.json");
    private UserSettingsService Settings() => new(SettingsPath, NullLogger<UserSettingsService>.Instance);
    private UiStateService Ui() => new(UiPath, NullLogger<UiStateService>.Instance);

    [Fact]
    public void Without_a_file_the_defaults_are_used_and_nothing_is_written()
    {
        var settings = Settings();
        Assert.Equal(MediaTime.FromSeconds(-70), settings.Current.HeartRateDisplayOffset);
        Assert.False(settings.Current.HoldProposalsEnabled);
        Assert.Null(settings.Current.Language);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void A_change_is_written_and_read_back_by_the_next_start()
    {
        var settings = Settings();
        var changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Update(s => s with { HeartRateDisplayOffsetTicks = MediaTime.FromSeconds(-45).Ticks, HoldProposalsEnabled = true, Language = " en " });
        settings.Update(s => s);                                         // equal: nothing raised
        Assert.Equal(1, changes);

        var next = Settings();
        Assert.Equal(MediaTime.FromSeconds(-45), next.Current.HeartRateDisplayOffset);
        Assert.True(next.Current.HoldProposalsEnabled);
        Assert.Equal("en", next.Current.Language);
        Assert.Equal(UserFileState.Usable, next.FileState);
    }

    [Fact]
    public void The_offset_is_kept_inside_its_bounds()
    {
        var settings = Settings();
        settings.Update(s => s with { HeartRateDisplayOffsetTicks = MediaTime.FromSeconds(-5000).Ticks });
        Assert.Equal(-UserSettings.MaxHeartRateDisplayOffset.Ticks, settings.Current.HeartRateDisplayOffsetTicks);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_next_write_makes_a_new_one()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{ damaged");
        var settings = Settings();
        Assert.Equal(UserFileState.Damaged, settings.FileState);
        Assert.Equal(new UserSettings(), settings.Current);
        Assert.True(File.Exists(SettingsPath + ".damaged"));

        settings.Update(s => s with { HoldProposalsEnabled = true });
        Assert.True(Settings().Current.HoldProposalsEnabled);
    }

    [Fact]
    public void A_newer_file_is_never_overwritten()
    {
        Directory.CreateDirectory(_folder);
        var newer = "{\"format\":\"" + UserSettingsService.FormatId + "\",\"formatVersion\":99,\"value\":{\"x\":1}}";
        File.WriteAllText(SettingsPath, newer);
        var settings = Settings();
        Assert.Equal(UserFileState.Newer, settings.FileState);
        settings.Update(s => s with { HoldProposalsEnabled = true });
        Assert.True(settings.Current.HoldProposalsEnabled);              // for this run
        Assert.Equal(newer, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_file_of_another_format_is_damaged()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{\"format\":\"AiVideoEditor.RecentProjects\",\"formatVersion\":1,\"value\":{}}");
        Assert.Equal(UserFileState.Damaged, Settings().FileState);
    }

    [Fact]
    public void The_layout_round_trips_with_its_sections_and_scroll_positions()
    {
        using (var ui = Ui())
        {
            ui.Update(s => s with
            {
                Window = new WindowPlacement(10, 20, 1500, 950, true),
                InspectorWidth = 340, MediaBrowserWidth = 280, TimelineHeight = 300,
                InspectorSections = s.InspectorSections.SetItem("transform", true).SetItem("holds", false),
                InspectorScroll = s.InspectorScroll.SetItem("video", 412.5)
            });
            ui.Flush();
        }
        using var back = Ui();
        Assert.Equal(new WindowPlacement(10, 20, 1500, 950, true), back.Current.Window);
        Assert.Equal(340, back.Current.InspectorWidth);
        Assert.True(back.Current.InspectorSections["transform"]);
        Assert.False(back.Current.InspectorSections["holds"]);
        Assert.Equal(412.5, back.Current.InspectorScroll["video"]);
    }

    [Fact]
    public void A_burst_of_layout_changes_is_written_once_after_the_delay()
    {
        using var ui = Ui();
        for (var i = 0; i < 20; i++)
        {
            var width = 300 + i;
            ui.Update(s => s with { InspectorWidth = width });
        }
        Assert.False(File.Exists(UiPath));                               // not yet: the delay
        var deadline = DateTime.UtcNow + UiStateService.WriteDelay + TimeSpan.FromSeconds(5);
        while (!File.Exists(UiPath) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        using var back = Ui();
        Assert.Equal(319, back.Current.InspectorWidth);
    }

    [Fact]
    public void Equal_layouts_compare_equal_with_their_dictionaries()
    {
        var a = new UiState { InspectorSections = ImmutableSortedDictionary<string, bool>.Empty.Add("x", true) };
        var b = new UiState { InspectorSections = ImmutableSortedDictionary<string, bool>.Empty.Add("x", true) };
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { InspectorSections = b.InspectorSections.SetItem("x", false) });
    }
}
