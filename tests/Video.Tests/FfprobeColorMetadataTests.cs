using System.Text.Json;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 18 Step 18.4 (D033 T1): the analysis reads a video's colour — pixel format, bit depth, transfer, primaries,
/// matrix, range — and, for HLG / PQ, the HDR10 static metadata (from the stream, else the first frame), the Dolby
/// Vision profile and HDR Vivid's presence. Real ffprobe on synthetic files (<see cref="HdrMedia"/>); the side data of
/// phone files (Dolby Vision, HDR Vivid), which can't be synthesized here, from their recorded ffprobe JSON.
/// </summary>
public sealed class FfprobeColorMetadataTests
{
    private static readonly FfprobeMediaAnalysisService Probe = FfmpegTools.Analysis();

    private static async Task<MediaMetadata> Analyze(string path)
    {
        var result = await Probe.AnalyzeAsync(path);
        Assert.Equal(Core.Interfaces.MediaAnalysisOutcome.Success, result.Outcome);
        return result.Metadata!;
    }

    [FfmpegFact]
    public async Task An_HLG_file_is_read_as_HLG_BT_2020_10_bit_and_classified_HDR()
    {
        var m = await Analyze(HdrMedia.Hlg);
        Assert.Equal(("yuv420p10le", 10, "arib-std-b67", "bt2020", "bt2020nc", "tv"),
            (m.PixelFormat, m.BitDepth, m.ColorTransfer, m.ColorPrimaries, m.ColorMatrix, m.ColorRange));
        Assert.Null(m.MasteringMaxLuminance);
        Assert.Null(m.MaxContentLightLevel);
        Assert.False(m.NeedsColorProbe);
        Assert.Equal(new HdrConversion(HdrTransfer.Hlg, 1000, false), HdrColor.Classify(MediaKind.Video, m).Conversion);
    }

    [FfmpegFact]
    public async Task A_PQ_file_with_HDR10_metadata_gets_its_mastering_maximum_and_MaxCLL_from_the_first_frame()
    {
        var m = await Analyze(HdrMedia.PqWithMetadata);
        Assert.Equal("smpte2084", m.ColorTransfer);
        Assert.Equal(4000.0, m.MasteringMaxLuminance);
        Assert.Equal(1500, m.MaxContentLightLevel);
        Assert.Equal(1500, HdrColor.Classify(MediaKind.Video, m).Conversion!.PeakNits);
    }

    [FfmpegFact]
    public async Task A_PQ_file_without_metadata_falls_back_to_1000_nit()
    {
        var m = await Analyze(HdrMedia.PqWithoutMetadata);
        Assert.Null(m.MasteringMaxLuminance);
        Assert.Null(m.MaxContentLightLevel);
        Assert.Equal(1000, HdrColor.Classify(MediaKind.Video, m).Conversion!.PeakNits);
    }

    [FfmpegFact]
    public async Task SDR_and_an_unsupported_HDR_variant_are_classified_as_such()
    {
        var sdr = await Analyze(HdrMedia.Sdr10);
        Assert.Equal(("bt709", "bt709", 10), (sdr.ColorTransfer, sdr.ColorPrimaries, sdr.BitDepth!.Value));
        Assert.Equal(HdrKind.Sdr, HdrColor.Classify(MediaKind.Video, sdr).Kind);

        var variant = await Analyze(HdrMedia.HlgBt709Primaries);
        var classification = HdrColor.Classify(MediaKind.Video, variant);
        Assert.Equal(HdrKind.UnsupportedHdr, classification.Kind);
        Assert.Contains("BT.709 primaries", classification.Reason);
    }

    [Fact]
    public void Dolby_Vision_and_HDR_Vivid_side_data_of_phone_files_are_read()
    {
        // Recorded with ffprobe 9.0.1 from the product owner's files (Phase 18 pre-flight): a Vivo X300 Pro stream's
        // configuration record, a Huawei P60 Pro first frame's side data.
        const string vivoStream = """
            [{"side_data_type":"DOVI configuration record","dv_version_major":1,"dv_version_minor":0,"dv_profile":8,"dv_level":9,
              "rpu_present_flag":1,"el_present_flag":0,"bl_present_flag":1,"dv_bl_signal_compatibility_id":4},
             {"side_data_type":"Display Matrix","displaymatrix":"\n00000000:            0       65536           0\n","rotation":-90}]
            """;
        const string huaweiFrame = """
            [{"side_data_type":"3x3 displaymatrix","rotation":-90},
             {"side_data_type":"Mastering display metadata","max_luminance":"10000000/10000","min_luminance":"50/10000"},
             {"side_data_type":"Content light level metadata","max_content":0,"max_average":0},
             {"side_data_type":"HDR Dynamic Metadata CUVA 005.1 2021 (Vivid)"}]
            """;
        var vivo = new MediaMetadata { ColorTransfer = "arib-std-b67" };
        FfprobeMediaAnalysisService.ApplyHdrSideData(vivo, JsonSerializer.Deserialize<List<FfprobeSideData>>(vivoStream));
        Assert.Equal((8, 4), (vivo.DolbyVisionProfile!.Value, vivo.DolbyVisionCompatibility!.Value));
        Assert.False(vivo.HasHdrVivid);

        var huawei = new MediaMetadata { ColorTransfer = "arib-std-b67" };
        FfprobeMediaAnalysisService.ApplyHdrSideData(huawei, JsonSerializer.Deserialize<List<FfprobeSideData>>(huaweiFrame));
        Assert.Equal(1000.0, huawei.MasteringMaxLuminance);
        Assert.Equal(0, huawei.MaxContentLightLevel);
        Assert.True(huawei.HasHdrVivid);
        Assert.Null(huawei.DolbyVisionProfile);
    }

    [Fact]
    public void The_streams_values_win_over_the_first_frames()
    {
        var m = new MediaMetadata { MasteringMaxLuminance = 4000, MaxContentLightLevel = 1200 };
        FfprobeMediaAnalysisService.ApplyHdrSideData(m, JsonSerializer.Deserialize<List<FfprobeSideData>>("""
            [{"side_data_type":"Mastering display metadata","max_luminance":"10000000/10000"},
             {"side_data_type":"Content light level metadata","max_content":300}]
            """));
        Assert.Equal((4000.0, 1200), (m.MasteringMaxLuminance!.Value, m.MaxContentLightLevel!.Value));
    }
}
