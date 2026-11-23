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
        Assert.False(settings.Current.RouteProposalsEnabled);                 // D072 amendment 2: off by default
        Assert.Null(settings.Current.Language);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void A_change_is_written_and_read_back_by_the_next_start()
    {
        var settings = Settings();
        var changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.Update(s => s with { HeartRateDisplayOffsetTicks = MediaTime.FromSeconds(-45).Ticks, RouteProposalsEnabled = true, Language = " en " });
        settings.Update(s => s);                                         // equal: nothing raised
        Assert.Equal(1, changes);

        var next = Settings();
        Assert.Equal(MediaTime.FromSeconds(-45), next.Current.HeartRateDisplayOffset);
        Assert.True(next.Current.RouteProposalsEnabled);
        Assert.Equal("en", next.Current.Language);
        Assert.Equal(UserFileState.Usable, next.FileState);
    }

    [Fact]
    public void A_file_saved_before_the_per_maker_offsets_keeps_its_coros_value_and_gets_minus_twenty_for_the_others()
    {
        // D062 amendment 1: the COROS value keeps its old name; a file without the new values gets their defaults.
        Settings().Update(s => s with { HeartRateDisplayOffsetTicks = MediaTime.FromSeconds(-45).Ticks });
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var removed = RemoveKeys(json, "garminHeartRateDisplayOffsetTicks", "otherHeartRateDisplayOffsetTicks");
        Assert.Equal(2, removed);
        File.WriteAllText(SettingsPath, json.ToJsonString());

        var old = Settings().Current;
        Assert.Equal(MediaTime.FromSeconds(-45), old.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Coros));
        Assert.Equal(MediaTime.FromSeconds(-20), old.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Garmin));
        Assert.Equal(MediaTime.FromSeconds(-20), old.HeartRateDisplayOffsetFor(Core.Telemetry.FitVendor.Other));
    }

    [Fact]
    public void A_file_of_the_test_builds_with_make_proxies_still_loads_with_its_values()
    {
        // D071 amendment 3: the 1.4.0 / 1.4.1 builds wrote «makeProxies»; the setting is gone, the file must still be read.
        Settings().Update(s => s with { PreviewQuality = Core.Playback.PreviewQuality.Low, RouteProposalsEnabled = true });
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var holder = Holder(json, "previewQuality") ?? throw new InvalidOperationException("previewQuality not saved");
        holder["makeProxies"] = false;
        File.WriteAllText(SettingsPath, json.ToJsonString());

        var reread = Settings();
        Assert.Equal(UserFileState.Usable, reread.FileState);
        Assert.Equal(Core.Playback.PreviewQuality.Low, reread.Current.PreviewQuality);
        Assert.True(reread.Current.RouteProposalsEnabled);

        static System.Text.Json.Nodes.JsonObject? Holder(System.Text.Json.Nodes.JsonNode node, string key) => node switch
        {
            System.Text.Json.Nodes.JsonObject o when o.ContainsKey(key) => o,
            System.Text.Json.Nodes.JsonObject o => o.Select(p => p.Value is null ? null : Holder(p.Value, key)).FirstOrDefault(h => h is not null),
            _ => null
        };
    }

    [Fact]
    public void Files_of_1_3_and_of_the_test_builds_1_5_load_with_the_route_proposals_off()
    {
        // D072 amendment 2: «Предлагать трассу» is saved as «suggestRoute», off by default. 1.3's «holdProposalsEnabled» (D047's
        // proposals of every hold) and 1.5.0 / 1.5.1's «routeProposalsEnabled» (written on by default) are no longer read.
        Settings().Update(s => s with { CanvasSnapPixels = 5 });
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(1, RemoveKeys(json, "suggestRoute"));
        var holder = json["value"]!.AsObject();
        holder["holdProposalsEnabled"] = true;
        holder["routeProposalsEnabled"] = true;
        File.WriteAllText(SettingsPath, json.ToJsonString());

        var reread = Settings();
        Assert.Equal(UserFileState.Usable, reread.FileState);
        Assert.Equal(5, reread.Current.CanvasSnapPixels);
        Assert.False(reread.Current.RouteProposalsEnabled);

        reread.Update(s => s with { RouteProposalsEnabled = true });          // the user's tick is kept under the new key
        Assert.Contains("\"suggestRoute\": true", File.ReadAllText(SettingsPath).Replace("\"suggestRoute\":true", "\"suggestRoute\": true"));
        Assert.True(Settings().Current.RouteProposalsEnabled);
    }

    /// <summary>Removes the properties named <paramref name="names"/> (any case) wherever they are; how many were removed.</summary>
    private static int RemoveKeys(System.Text.Json.Nodes.JsonNode node, params string[] names)
    {
        var count = 0;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (names.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)))
                {
                    obj.Remove(key);
                    count++;
                }
                else if (obj[key] is { } child) count += RemoveKeys(child, names);
            }
        }
        return count;
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

        settings.Update(s => s with { RouteProposalsEnabled = true });
        Assert.True(Settings().Current.RouteProposalsEnabled);
    }

    [Fact]
    public void A_newer_file_is_never_overwritten()
    {
        Directory.CreateDirectory(_folder);
        var newer = "{\"format\":\"" + UserSettingsService.FormatId + "\",\"formatVersion\":99,\"value\":{\"x\":1}}";
        File.WriteAllText(SettingsPath, newer);
        var settings = Settings();
        Assert.Equal(UserFileState.Newer, settings.FileState);
        settings.Update(s => s with { RouteProposalsEnabled = true });
        Assert.True(settings.Current.RouteProposalsEnabled);            // for this run
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
        // The write runs on a thread-pool timer; on a loaded CI runner it can start seconds late (run 44097888225 timed out
        // after 1 + 5 s), so the wait is generous — the assertions below are unchanged.
        var deadline = DateTime.UtcNow + UiStateService.WriteDelay + TimeSpan.FromSeconds(30);
        while (!File.Exists(UiPath) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.True(File.Exists(UiPath), "the layout was not written");
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

    // --- D076 §4.3: the saved export presets --------------------------------------------------------------------------

    [Fact]
    public void Saved_export_presets_are_written_by_name_and_read_back_in_their_order()
    {
        var mine = new Core.Entities.SavedExportPreset("Архив", Core.Export.ExportPresets.Maximum with { Codec = Core.Entities.ExportVideoCodec.Av1, Container = Core.Entities.ExportContainer.Mkv });
        var fast = new Core.Entities.SavedExportPreset("Telegram", Core.Export.ExportPresets.Fast);
        Settings().Update(s => s with { ExportPresets = new[] { mine, fast } });

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"codec\": \"Av1\"", text);
        Assert.Contains("\"container\": \"Mkv\"", text);
        Assert.Contains("\"quality\": \"Maximum\"", text);
        Assert.DoesNotContain("\"crf\"", text);                                  // computed members are not written
        var next = Settings();
        Assert.Equal(new[] { mine, fast }, next.Current.ExportPresets);
        Assert.Equal(UserFileState.Usable, next.FileState);
    }

    [Fact]
    public void A_saved_preset_that_cannot_be_read_is_dropped_alone_and_the_other_settings_stay()
    {
        var good = new Core.Entities.SavedExportPreset("Good", Core.Export.ExportPresets.High);
        Settings().Update(s => s with { Language = "en", ExportPresets = new[] { good, good with { Name = "Bad" }, good with { Name = "Mov" } } });
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var presets = root["value"]!["exportPresets"]!.AsArray();
        presets[1]!["settings"]!["codec"] = "Vp9";                                // a name of a newer version
        presets[2]!["settings"]!["codec"] = "Av1";
        presets[2]!["settings"]!["container"] = "Mov";                            // valid names, not a valid preset
        File.WriteAllText(SettingsPath, root.ToJsonString());

        var next = Settings();

        Assert.Equal(new[] { good }, next.Current.ExportPresets);
        Assert.Equal("en", next.Current.Language);
        Assert.Equal(UserFileState.Usable, next.FileState);
        Assert.False(File.Exists(SettingsPath + ".damaged"));
    }
}
