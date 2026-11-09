using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>D057 §3.2: several files go onto the timeline in the order of their recording — the tag (as the end), else the
/// file name's time; files without a time follow in the list's order; equal times keep it.</summary>
public class MediaRecordingOrderTests
{
    private static readonly DateTimeOffset W0 = new(2026, 9, 27, 7, 0, 0, TimeSpan.Zero);

    private static MediaAsset Video(string name, DateTimeOffset? tagEnd = null, double seconds = 60) => new()
    {
        FilePath = @"C:\media\" + name, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata
        {
            Duration = MediaTime.FromSeconds(seconds), FrameRate = FrameRate.Fps30,
            RecordingTimeProbe = tagEnd is { } t
                ? RecordingTimeProbe.Found(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format)
                : RecordingTimeProbe.Absent
        }
    };

    [Fact]
    public void Files_are_ordered_by_their_tags_whatever_the_list_order()
    {
        var a = Video("a.mp4", W0.AddMinutes(10));
        var b = Video("b.mp4", W0.AddMinutes(2));
        var c = Video("c.mp4", W0.AddMinutes(5));
        Assert.Equal(new[] { b, c, a }, MediaRecordingOrder.Order(new[] { a, b, c }, null));
    }

    [Fact]
    public void Without_a_tag_the_file_name_time_orders_them()
    {
        var late = Video("video_20260927_101034.mp4");
        var early = Video("video_20260927_100628.mp4");
        Assert.Equal(new[] { early, late }, MediaRecordingOrder.Order(new[] { late, early }, TimeSpan.FromHours(3)));
        Assert.Equal(new[] { early, late }, MediaRecordingOrder.Order(new[] { late, early }, null));
    }

    [Fact]
    public void Files_without_a_time_follow_in_the_list_order_and_equal_times_keep_it()
    {
        var image = new MediaAsset { FilePath = @"C:\media\x.png", Kind = MediaKind.Image };
        var untimed = Video("clip.mp4");
        var first = Video("p.mp4", W0);
        var same = Video("q.mp4", W0);
        Assert.Equal(new[] { first, same, untimed, image }, MediaRecordingOrder.Order(new[] { untimed, first, image, same }, null));
    }
}
