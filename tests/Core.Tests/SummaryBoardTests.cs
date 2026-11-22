using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D073 §2: «Итоги»'s rows — the routes started before the template and not before the previous «Итоги»; the climb time
/// <c>S − Ae</c> in the anchored clip's source time (the orange's end, else the route's <c>T</c>); the heart rate of the FIT's
/// samples in that window (with the display offset); the attempts of one boulder (the same number and grade) one row with the
/// best result, the first top's time, the stated or counted attempts (a Flash: one); the totals.
/// </summary>
public class SummaryBoardTests
{
    private static readonly FrameRate Rate = FrameRate.Fps30;
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static MediaTime S(double seconds) => MediaTime.FromSeconds(seconds);

    private readonly Sequence _sequence = new();
    private readonly Track _videos = new() { Type = TrackType.Video, Name = "V1" };
    private readonly Track _templates = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Guid _asset = Guid.NewGuid();
    private HeartRateBinding? _binding;

    public SummaryBoardTests()
    {
        _sequence.VideoTracks.Add(_videos);
        _sequence.VideoTracks.Add(_templates);
    }

    /// <summary>A 20 s video clip at <paramref name="startSeconds"/> with one route: its orange <c>[1 s, 6 s)</c> of source
    /// (when <paramref name="orange"/>), its green over the whole clip with the status at 18 s of the template's clock.</summary>
    private TemplateClip Route(double startSeconds, string number, string grade, BoulderStatus status, bool orange = true,
        MediaTime? routeStart = null, int? attempts = null, ClipSpeed speed = default, string accent = "#FF1744")
    {
        var routeId = Guid.NewGuid();
        var clip = new VideoClip
        {
            MediaAssetId = _asset, TimelineStart = S(startSeconds), Duration = F(600), Speed = speed,
            Routes = orange
                ? [new HoldMarkup { Id = routeId, Animation = new HoldAnimation { Mode = HoldAnimationMode.Duration, StartSourceTime = S(1), Duration = S(5) } }]
                : []
        };
        _videos.Clips.Add(clip);
        var template = new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = clip.TimelineStart, Duration = F(600), Route = number, Grade = grade, Status = status,
            StatusAt = S(18), AnchorClipId = clip.Id, AnchorRouteId = routeId, RouteStart = routeStart, Attempts = attempts, AccentHex = accent
        };
        _templates.Clips.Add(template);
        return template;
    }

    private void Summary(double startSeconds) =>
        _templates.Clips.Add(new TemplateClip { Kind = TemplateKind.Summary, TimelineStart = S(startSeconds), Duration = F(300) });

    /// <summary>A training whose clock is the video's (zero = the clip's source 0 of each clip is <see cref="Start"/>): one
    /// sample a second, 100 + the second.</summary>
    private void Training(MediaTime? displayOffset = null)
    {
        var workout = new ClimbingWorkout
        {
            DeviceName = "COROS", Manufacturer = 294, Product = null, StartUtc = Start, EndUtc = Start.AddSeconds(600), UtcOffset = TimeSpan.Zero,
            ElapsedTime = S(600), TimerTime = S(600), Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null,
            HeartRate = Enumerable.Range(0, 600).Select(i => new HeartRateSample(Start.AddSeconds(i), 100 + i)).ToList(),
            Pauses = Array.Empty<WorkoutInterval>(), Segments = Array.Empty<WorkoutSegment>(), Warnings = Array.Empty<TrainingFileWarning>()
        };
        _binding = new HeartRateBinding(workout, Start, MediaTime.Zero) { DisplayOffset = displayOffset ?? MediaTime.Zero };
    }

    private SummaryBoard Board(double summarySeconds) => SummaryBoard.Of(_sequence, S(summarySeconds), Rate, id => id == _asset ? _binding : null);

    [Fact]
    public void The_climb_time_runs_from_the_oranges_end_to_the_lilacs_start_with_its_heart_rate()
    {
        Training();
        Route(0, "7", "6B", BoulderStatus.Top);
        Summary(30);

        var row = Assert.Single(Board(30).Rows);
        Assert.Equal(("7", "6B", BoulderStatus.Top, 1), (row.Route, row.Grade, row.Result, row.Attempts));
        Assert.Equal(S(12), row.ClimbTime);                     // S 18 s − Ae 6 s
        Assert.Equal((112, 117), (row.AverageHeartRate, row.MaxHeartRate));   // samples 6 … 17 s: 106 … 117
    }

    [Fact]
    public void Without_an_orange_the_routes_start_is_used_and_without_either_no_time()
    {
        Route(0, "1", "", BoulderStatus.Top, orange: false, routeStart: S(8));
        Route(30, "2", "", BoulderStatus.Top, orange: false);
        var rows = Board(60).Rows;
        Assert.Equal(S(10), rows[0].ClimbTime);
        Assert.Null(rows[1].ClimbTime);
    }

    [Fact]
    public void No_status_no_lilac_no_time_and_a_slowed_clip_counts_source_time()
    {
        Route(0, "1", "", BoulderStatus.None);
        Route(30, "2", "", BoulderStatus.Attempt, speed: ClipSpeed.FromSteps(10));   // 0.5×: the frames of the template are half the source
        var rows = Board(80).Rows;
        Assert.Equal((BoulderStatus.None, (MediaTime?)null), (rows[0].Result, rows[0].ClimbTime));
        // S at 18 s of the template = 9 s of source; Ae: the orange starts at 1 s of source and lasts 5 s of the timeline
        // (D048 §3.4) = 2.5 s of source, so 3.5 s — the climb is 5.5 s of source, not the 11 s the timeline shows.
        Assert.Equal(S(5.5), rows[1].ClimbTime);
    }

    [Fact]
    public void Only_routes_before_the_template_and_after_the_previous_summary_are_listed()
    {
        Route(0, "1", "", BoulderStatus.Top);
        Summary(25);
        Route(30, "2", "", BoulderStatus.Top);
        Route(60, "3", "", BoulderStatus.Top);
        Summary(90);
        Route(100, "4", "", BoulderStatus.Top);

        Assert.Equal(new[] { "1" }, Board(25).Rows.Select(r => r.Route));
        Assert.Equal(new[] { "2", "3" }, Board(90).Rows.Select(r => r.Route));
    }

    [Fact]
    public void Attempts_of_one_boulder_are_one_row_with_the_best_result_and_the_first_tops_time()
    {
        Training();
        Route(0, "7", "6b", BoulderStatus.Attempt, accent: "#2979FF");
        Route(30, "7", "6B ", BoulderStatus.Top, accent: "#00E676");
        Route(60, "7", "6B", BoulderStatus.Attempt);
        Route(90, "", "6B", BoulderStatus.Top);
        Route(120, "", "6B", BoulderStatus.Top);
        Route(150, "8", "6B", BoulderStatus.Attempt);

        var rows = Board(200).Rows;
        Assert.Equal(4, rows.Length);                                     // 7 / 6B once, the two unnumbered apart, 8
        Assert.Equal((BoulderStatus.Top, 3, "#00E676"), (rows[0].Result, rows[0].Attempts, rows[0].AccentHex));
        Assert.Equal(S(12), rows[0].ClimbTime);
        Assert.Equal(117, rows[0].MaxHeartRate);                          // every clip is the same file: the window 6 … 17 s
        Assert.Equal((BoulderStatus.Attempt, 1), (rows[3].Result, rows[3].Attempts));
    }

    [Fact]
    public void Stated_attempts_replace_the_count_and_a_flash_is_one()
    {
        Route(0, "1", "", BoulderStatus.Attempt, attempts: 5);
        Route(30, "1", "", BoulderStatus.Top);
        Route(60, "2", "", BoulderStatus.Flash, attempts: 3);
        var rows = Board(90).Rows;
        Assert.Equal(5, rows[0].Attempts);
        Assert.Equal((BoulderStatus.Flash, 1), (rows[1].Result, rows[1].Attempts));
    }

    [Fact]
    public void The_totals_count_tops_with_flashes_and_sum_every_attempts_time()
    {
        Training();
        Route(0, "1", "", BoulderStatus.Attempt);
        Route(30, "1", "", BoulderStatus.Top);
        Route(60, "2", "", BoulderStatus.Flash);
        Route(90, "3", "", BoulderStatus.None);

        var totals = Board(120).Totals;
        Assert.Equal((3, 2, 1), (totals.Routes, totals.Tops, totals.Flashes));
        Assert.Equal(S(36), totals.OnRoutes);                             // three climbs of 12 s
        Assert.Equal((112, 117), (totals.AverageHeartRate, totals.MaxHeartRate));   // the same file: every window 6 … 17 s
        Assert.True(Board(120).ShowsHeartRate);
    }

    [Fact]
    public void Without_a_training_the_heart_rate_is_not_shown_and_the_display_offset_moves_the_window()
    {
        Route(0, "1", "", BoulderStatus.Top);
        Assert.False(Board(30).ShowsHeartRate);

        Training(displayOffset: S(-2));                                   // shown 2 s earlier: the samples of 8 … 19 s
        Assert.Equal(119, Board(30).Rows[0].MaxHeartRate);
    }

    /// <summary>A route whose 20 s clip was split at <paramref name="cutSeconds"/> (after its orange): the left clip keeps the
    /// orange, both clips keep the route id, the green is cut in two parts whose clock continues — the status in the right part.</summary>
    private void SplitRoute(double startSeconds, string number, double cutSeconds = 10)
    {
        var routeId = Guid.NewGuid();
        var left = new VideoClip
        {
            MediaAssetId = _asset, TimelineStart = S(startSeconds), Duration = S(cutSeconds),
            Routes = [new HoldMarkup { Id = routeId, Animation = new HoldAnimation { Mode = HoldAnimationMode.Duration, StartSourceTime = S(1), Duration = S(5) } }]
        };
        var right = new VideoClip
        {
            MediaAssetId = _asset, TimelineStart = S(startSeconds + cutSeconds), Duration = S(20 - cutSeconds), SourceIn = S(cutSeconds),
            Routes = [new HoldMarkup { Id = routeId }]
        };
        _videos.Clips.Add(left);
        _videos.Clips.Add(right);
        _templates.Clips.Add(new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = left.TimelineStart, Duration = S(cutSeconds), Route = number, Status = BoulderStatus.Top,
            StatusAt = S(18), AnchorClipId = left.Id, AnchorRouteId = routeId
        });
        _templates.Clips.Add(new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = right.TimelineStart, Duration = S(20 - cutSeconds), CounterStart = S(cutSeconds), Route = number,
            Status = BoulderStatus.Top, StatusAt = S(18), AnchorClipId = right.Id, AnchorRouteId = routeId
        });
    }

    [Theory]
    [InlineData("7")]
    [InlineData("")]
    public void A_route_whose_clip_was_split_is_one_attempt_with_its_time(string number)
    {
        Training();
        SplitRoute(0, number);
        var row = Assert.Single(Board(30).Rows);
        Assert.Equal((BoulderStatus.Top, 1), (row.Result, row.Attempts));
        Assert.Equal(S(12), row.ClimbTime);                     // S in the right part (18 s of source), Ae on the left clip (6 s)
        Assert.Equal(117, row.MaxHeartRate);
        Assert.Equal(S(12), Board(30).Totals.OnRoutes);
    }

    [Fact]
    public void A_copy_of_a_routes_clip_keeps_the_route_id_but_is_another_attempt()
    {
        var original = Route(0, "", "", BoulderStatus.Top);
        var copy = Route(30, "", "", BoulderStatus.Top);
        copy.AnchorRouteId = original.AnchorRouteId;             // a copy keeps its route's id
        Assert.Equal(2, Board(60).Rows.Length);
    }

    [Fact]
    public void A_status_beyond_the_area_gives_its_result_without_a_time_and_a_free_area_is_listed()
    {
        var area = Route(0, "1", "", BoulderStatus.Top);
        area.StatusAt = S(25);                                  // after the area's end: no lilac
        _templates.Clips.Add(new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = S(30), Duration = S(5), Route = "2", Status = BoulderStatus.Attempt });
        var rows = Board(60).Rows;
        Assert.Equal((BoulderStatus.Top, (MediaTime?)null), (rows[0].Result, rows[0].ClimbTime));
        Assert.Equal(("2", BoulderStatus.Attempt, (MediaTime?)null), (rows[1].Route, rows[1].Result, rows[1].ClimbTime));
    }

    [Fact]
    public void No_routes_give_an_empty_board()
    {
        Summary(10);
        var board = Board(10);
        Assert.Empty(board.Rows);
        Assert.Equal(new SummaryTotals(0, 0, 0, null, null, null), board.Totals);
    }
}
