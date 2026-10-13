using System.Text.Json.Nodes;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 18 Step 18.4 (D033 HQ2, T2): the colour of the media metadata in <c>project.json</c> v4 — optional properties,
/// written only when known (a file without them is written exactly as before), read back with the bit depth from the
/// pixel format, the HDR classification unchanged by a round trip; metadata saved before Phase 18 is kept and flagged
/// for the background colour probe.
/// </summary>
public class MediaColorPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;

    private static Core.Entities.Project Sample()
    {
        var project = ProjectTestData.Build(Folder + @"\media");
        var video = project.MediaAssets[0].Metadata!;
        video.PixelFormat = "yuv420p10le";
        video.BitDepth = 10;
        video.ColorTransfer = "arib-std-b67";
        video.ColorPrimaries = "bt2020";
        video.ColorMatrix = "bt2020nc";
        video.ColorRange = "tv";
        video.MasteringMaxLuminance = 1000;
        video.MaxContentLightLevel = 0;
        video.DolbyVisionProfile = 8;
        video.DolbyVisionCompatibility = 4;
        video.HasHdrVivid = true;
        return project;
    }

    private static Core.Entities.Project RoundTrip(Core.Entities.Project project) =>
        ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project, Folder), Folder, AllExist);

    [Fact]
    public void Colour_round_trips_and_the_bit_depth_follows_from_the_pixel_format()
    {
        var m = RoundTrip(Sample()).MediaAssets[0].Metadata!;

        Assert.Equal(("yuv420p10le", 10, "arib-std-b67", "bt2020", "bt2020nc", "tv"),
            (m.PixelFormat, m.BitDepth, m.ColorTransfer, m.ColorPrimaries, m.ColorMatrix, m.ColorRange));
        Assert.Equal((1000.0, 0, 8, 4, true), (m.MasteringMaxLuminance!.Value, m.MaxContentLightLevel!.Value, m.DolbyVisionProfile!.Value,
            m.DolbyVisionCompatibility!.Value, m.HasHdrVivid));
        Assert.False(m.NeedsColorProbe);
        Assert.Equal(HdrColor.Classify(MediaKind.Video, Sample().MediaAssets[0].Metadata),
            HdrColor.Classify(MediaKind.Video, m));
        var sample = Sample();
        Assert.Equal(ProjectSerializer.Serialize(sample, Folder), ProjectSerializer.Serialize(RoundTrip(sample), Folder));
    }

    [Fact]
    public void Unknown_colour_properties_are_not_written_so_older_metadata_is_saved_exactly_as_before()
    {
        var plain = ProjectTestData.Build(Folder + @"\media");   // no colour at all
        var json = ProjectSerializer.Serialize(plain, Folder);
        foreach (var name in new[] { "pixelFormat", "colorTransfer", "colorPrimaries", "colorMatrix", "colorRange",
                     "masteringMaxLuminance", "maxContentLightLevel", "dolbyVisionProfile", "dolbyVisionCompatibility", "hasHdrVivid", "bitDepth" })
            Assert.DoesNotContain(name, json);
        Assert.Equal(4, JsonNode.Parse(json)!["formatVersion"]!.GetValue<int>());

        var sdr = Sample();
        sdr.MediaAssets[0].Metadata!.HasHdrVivid = false;
        Assert.DoesNotContain("hasHdrVivid", ProjectSerializer.Serialize(sdr, Folder));   // written only when true
    }

    [Fact]
    public void Video_metadata_saved_before_Phase_18_is_kept_and_flagged_for_the_colour_probe()
    {
        var asset = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(ProjectTestData.Build(Folder + @"\media"), Folder),
            Folder, AllExist).MediaAssets[0];

        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.True(asset.Metadata!.NeedsColorProbe);
        Assert.Equal(HdrKind.Sdr, HdrColor.Classify(asset.Kind, asset.Metadata).Kind);   // SDR until the probe says otherwise
        Assert.False(asset.IsMetadataRefreshing);   // runtime state: never read from a file
    }

    [Fact]
    public void A_phase_18_file_read_by_a_reader_that_ignores_the_new_properties_keeps_every_other_value()
    {
        // D028 §5 / D014: a Phase 17 build ignores unknown properties. Removing them gives what it reads: the same
        // metadata without colour — the file is read without the setting, not wrongly.
        var sample = Sample();
        var root = JsonNode.Parse(ProjectSerializer.Serialize(sample, Folder))!.AsObject();
        var metadata = root["mediaAssets"]![0]!["metadata"]!.AsObject();
        foreach (var name in new[] { "pixelFormat", "colorTransfer", "colorPrimaries", "colorMatrix", "colorRange",
                     "masteringMaxLuminance", "maxContentLightLevel", "dolbyVisionProfile", "dolbyVisionCompatibility", "hasHdrVivid" })
            Assert.True(metadata.Remove(name), name);

        var stripped = ProjectSerializer.Serialize(
            ProjectSerializer.Deserialize(root.ToJsonString(), Folder, AllExist), Folder);
        var withoutColour = sample.MediaAssets[0].Metadata!;
        (withoutColour.PixelFormat, withoutColour.BitDepth, withoutColour.ColorTransfer, withoutColour.ColorPrimaries,
            withoutColour.ColorMatrix, withoutColour.ColorRange) = (null, null, null, null, null, null);
        (withoutColour.MasteringMaxLuminance, withoutColour.MaxContentLightLevel, withoutColour.DolbyVisionProfile,
            withoutColour.DolbyVisionCompatibility, withoutColour.HasHdrVivid) = (null, null, null, null, false);
        Assert.Equal(ProjectSerializer.Serialize(sample, Folder), stripped);
    }
}
