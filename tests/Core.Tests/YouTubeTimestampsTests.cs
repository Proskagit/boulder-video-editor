using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D057 §3.9: the YouTube timestamps — the first line at 00:00 («Intro» unless a route starts there), one line per route at
/// the left edge of its green area, «Трасса {номер} · {категория}», 🟢 / 🔴 for Top / Attempt, free areas included, the
/// parts of a split area one route, the range's times from In.
/// </summary>
public class YouTubeTimestampsTests
{
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private readonly Sequence _sequence = new();
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2" };

    public YouTubeTimestampsTests()
    {
        _sequence.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        _sequence.VideoTracks.Add(_v2);
    }

    private TemplateClip Area(double startSeconds, string route, string grade = "", BoulderStatus status = BoulderStatus.None,
        Guid? routeId = null, double counterStart = 0)
    {
        var area = new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromSeconds(startSeconds), Duration = MediaTime.FromSeconds(10),
            Route = route, Grade = grade, Status = status, AnchorClipId = routeId is null ? null : Guid.NewGuid(), AnchorRouteId = routeId,
            CounterStart = MediaTime.FromSeconds(counterStart)
        };
        _v2.Clips.Add(area);
        return area;
    }

    private string Text(MediaTime? from = null, MediaTime? to = null) => YouTubeTimestamps.Text(_sequence, Rate, from, to);

    [Fact]
    public void The_owners_example()
    {
        Area(42, "1", "6A", BoulderStatus.Top, Guid.NewGuid());
        Area(50, "3", "6B", BoulderStatus.Attempt, Guid.NewGuid());
        Area(95, "5", "6C");                                                  // free: included
        Area(110, "7", "7A", BoulderStatus.Top, Guid.NewGuid());
        Assert.Equal("00:00 Intro\n00:42 Трасса 1 · 6A 🟢\n00:50 Трасса 3 · 6B 🔴\n01:35 Трасса 5 · 6C\n01:50 Трасса 7 · 7A 🟢", Text());
    }

    [Fact]
    public void A_route_at_zero_needs_no_intro_and_missing_parts_drop_with_their_dot()
    {
        Area(0.4, "2", routeId: Guid.NewGuid());
        Area(20, "", "7A");
        Area(30, "");
        Assert.Equal("00:00 Трасса 2\n00:20 Трасса · 7A\n00:30 Трасса", Text());
    }

    [Fact]
    public void The_parts_of_a_split_area_are_one_route_and_two_routes_at_one_time_two_lines()
    {
        var route = Guid.NewGuid();
        Area(10, "4", routeId: route);
        Area(20, "4", routeId: route);                                         // the anchored area's second part
        Area(40, "8");
        Area(50, "8", counterStart: 10);                                       // the free area's second part (its clock goes on)
        Area(70, "9", routeId: Guid.NewGuid());
        Area(70, "9", routeId: Guid.NewGuid());                                // another route at the same time, same name
        Assert.Equal("00:00 Intro\n00:10 Трасса 4\n00:40 Трасса 8\n01:10 Трасса 9\n01:10 Трасса 9", Text());
    }

    [Fact]
    public void A_range_counts_from_in_and_leaves_out_the_routes_outside_it()
    {
        Area(10, "1", routeId: Guid.NewGuid());
        Area(100, "2", routeId: Guid.NewGuid());
        Area(3700, "3", routeId: Guid.NewGuid());
        Assert.Equal("00:00 Intro\n00:40 Трасса 2", Text(MediaTime.FromSeconds(60), MediaTime.FromSeconds(200)));
        Assert.Equal("1:01:40", YouTubeTimestamps.Format(MediaTime.FromSeconds(3700)));
        Assert.Equal("59:59", YouTubeTimestamps.Format(MediaTime.FromSeconds(3599.9)));
    }

    [Fact]
    public void Only_boulder_areas_count_never_the_other_templates()
    {
        _v2.Clips.Add(new TemplateClip { Kind = TemplateKind.Rec, TimelineStart = MediaTime.FromSeconds(5), Duration = MediaTime.FromSeconds(3) });
        Area(30, "1", routeId: Guid.NewGuid());
        Assert.Equal("00:00 Intro\n00:30 Трасса 1", Text());
    }
}
