using System.Text.Json.Serialization;

namespace AiVideoEditor.Video;

/// <summary>
/// Shape of `ffprobe -print_format json -show_format -show_streams` output.
/// Internal on purpose — nothing outside <see cref="FfprobeMediaAnalysisService"/>
/// should ever see ffprobe's JSON; everyone else deals in
/// <see cref="Core.Entities.MediaMetadata"/> / <see cref="Core.Interfaces.MediaAnalysisResult"/>.
/// Explicit <see cref="JsonPropertyNameAttribute"/>s are used instead of a naming
/// policy so this doesn't depend on any particular System.Text.Json version behavior.
/// </summary>
internal sealed class FfprobeOutput
{
    [JsonPropertyName("streams")]
    public List<FfprobeStream>? Streams { get; set; }

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("codec_type")]
    public string? CodecType { get; set; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>Fractional frame rate as a string, e.g. "30000/1001".</summary>
    [JsonPropertyName("r_frame_rate")]
    public string? RFrameRate { get; set; }

    /// <summary>Average frame rate as a string, e.g. "30000/1001"; "0/0" if unknown.</summary>
    [JsonPropertyName("avg_frame_rate")]
    public string? AvgFrameRate { get; set; }

    /// <summary>ffprobe reports this as a string even though it's numeric.</summary>
    [JsonPropertyName("sample_rate")]
    public string? SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    /// <summary>Stream side data; a "Display Matrix" entry carries the orientation.</summary>
    [JsonPropertyName("side_data_list")]
    public List<FfprobeSideData>? SideDataList { get; set; }

    /// <summary>Stream tags; older files may carry a legacy "rotate" tag (clockwise degrees).</summary>
    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>One side-data entry of a stream or frame.</summary>
internal sealed class FfprobeSideData
{
    [JsonPropertyName("side_data_type")]
    public string? SideDataType { get; set; }

    /// <summary>Counter-clockwise degrees of a display matrix, as ffprobe computes it.</summary>
    [JsonPropertyName("rotation")]
    public double? Rotation { get; set; }

    [JsonPropertyName("displaymatrix")]
    public string? DisplayMatrix { get; set; }
}

/// <summary>Shape of `ffprobe -show_frames` output (first frame only).</summary>
internal sealed class FfprobeFramesOutput
{
    [JsonPropertyName("frames")]
    public List<FfprobeFrame>? Frames { get; set; }
}

internal sealed class FfprobeFrame
{
    [JsonPropertyName("side_data_list")]
    public List<FfprobeSideData>? SideDataList { get; set; }
}

internal sealed class FfprobeFormat
{
    /// <summary>Seconds, as a string, e.g. "84.5".</summary>
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    /// <summary>Seconds with microsecond precision, as a string, e.g. "1.400000".</summary>
    [JsonPropertyName("start_time")]
    public string? StartTime { get; set; }

    /// <summary>Bits per second, as a string.</summary>
    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; set; }
}
