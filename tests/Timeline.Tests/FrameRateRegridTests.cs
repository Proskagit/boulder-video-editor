using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Re-grid of existing clips when the project frame rate gets fixed. Each scenario is
/// checked explicitly (order, no overlap, no zero-length clip, on-grid, atomic
/// failure) rather than relying on "nearest-frame rounding is monotonic".
/// </summary>
public class FrameRateRegridTests
{
    public static TheoryData<int, int, int, int> RatePairs => new()
    {
        { 30, 1, 24, 1 },          // 30 → 24
        { 30, 1, 30000, 1001 },    // 30 → 29.97
        { 24, 1, 30000, 1001 },    // 24 → 29.97
        { 30000, 1001, 24, 1 },    // 29.97 → 24
        { 25, 1, 60000, 1001 },
        { 60000, 1001, 24000, 1001 },
        { 24000, 1001, 25, 1 },
    };

    /// <summary>Runs the re-grid planner and, on success, applies it (as the service would).</summary>
    private static string? Regrid(TimelineFixture f, FrameRate to)
    {
        var plan = new EditPlan(f.Project.Timeline, f.Settings);
        plan.SetFrameRate(to, locked: true);
        var error = FrameRateRegrid.Plan(plan, to, f.FindAsset);
        if (error is null && !plan.IsEmpty)
            plan.BuildCommand("Regrid").Execute();
        return error;
    }

    private static void AssertLayout(TimelineFixture f, Track track, IReadOnlyList<Clip> expectedOrder)
    {
        Assert.Equal(expectedOrder.Select(c => c.Id), track.Clips.Select(c => c.Id));
        foreach (var clip in track.Clips)
        {
            Assert.True(clip.Duration.ToFrameFloor(f.Rate) >= 1 || clip.TimelineEnd.ToFrameFloor(f.Rate) - clip.TimelineStart.ToFrameFloor(f.Rate) >= 1);
            Assert.True(clip.Duration > MediaTime.Zero);
        }
        for (var i = 1; i < track.Clips.Count; i++)
            Assert.True(track.Clips[i - 1].TimelineEnd <= track.Clips[i].TimelineStart);
        f.AssertValid();
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void AdjacentClips_StayAdjacent_InOrder(int fromNum, int fromDen, int toNum, int toDen)
    {
        var from = new FrameRate(fromNum, fromDen);
        var to = new FrameRate(toNum, toDen);
        var f = new TimelineFixture();
        f.Settings.FrameRate = from;
        var img = f.Image();
        var clips = new List<Clip>();
        long frame = 0;
        // Every length is ≥ one target frame for all pairs (59.94 → 23.976 is 2.5×), so no
        // clip can collapse; sandwiched collapses are covered by dedicated tests below.
        foreach (var length in new[] { 7, 13, 3, 29, 4, 101 })
        {
            clips.Add(f.PlaceImageFrames(f.V1, img, frame, frame + length, from));
            frame += length;
        }

        Assert.Null(Regrid(f, to));

        Assert.Equal(to, f.Rate);
        AssertLayout(f, f.V1, clips);
        for (var i = 1; i < clips.Count; i++)
            Assert.Equal(clips[i - 1].TimelineEnd, clips[i].TimelineStart);
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void VeryShortClips_WithGaps_KeepAtLeastOneFrame(int fromNum, int fromDen, int toNum, int toDen)
    {
        var from = new FrameRate(fromNum, fromDen);
        var to = new FrameRate(toNum, toDen);
        var f = new TimelineFixture();
        f.Settings.FrameRate = from;
        var img = f.Image();
        var clips = new List<Clip>();
        for (var i = 0; i < 20; i++)
            clips.Add(f.PlaceImageFrames(f.V1, img, i * 3, i * 3 + 1, from)); // 1-frame clips, 2-frame gaps

        Assert.Null(Regrid(f, to));
        AssertLayout(f, f.V1, clips);
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void OneTickGap_DoesNotBecomeOverlap(int fromNum, int fromDen, int toNum, int toDen)
    {
        var from = new FrameRate(fromNum, fromDen);
        var to = new FrameRate(toNum, toDen);
        var f = new TimelineFixture();
        var img = f.Image();
        var boundary = MediaTime.FromFrame(10, from).Ticks;
        var a = f.Place<ImageClip>(f.V1, img, 0, boundary);
        var b = f.Place<ImageClip>(f.V1, img, boundary + 1, MediaTime.FromFrame(20, from).Ticks);
        f.Settings.FrameRate = from;

        Assert.Null(Regrid(f, to));

        AssertLayout(f, f.V1, new Clip[] { a, b });
        Assert.True(a.TimelineEnd <= b.TimelineStart);
    }

    [Theory]
    [InlineData(30, 1, 24, 1, 2)]           // 30f [2,3) = [66.7, 100) ms → both edges round to 24f 2
    [InlineData(60000, 1001, 24, 1, 0)]     // 59.94f [0,1) = [0, 16.7) ms → both edges round to 24f 0
    public void CollapsingClip_GrowsIntoFreeSpace(int fromNum, int fromDen, int toNum, int toDen, long startFrame)
    {
        var from = new FrameRate(fromNum, fromDen);
        var to = new FrameRate(toNum, toDen);
        var f = new TimelineFixture();
        f.Settings.FrameRate = from;
        var img = f.Image();

        var clip = f.PlaceImageFrames(f.V1, img, startFrame, startFrame + 1, from);
        var collapsedAt = clip.TimelineStart.ToNearestFrame(to);
        Assert.Equal(collapsedAt, clip.TimelineEnd.ToNearestFrame(to)); // premise: it would collapse

        Assert.Null(Regrid(f, to));

        Assert.Equal(MediaTime.FromFrame(collapsedAt, to), clip.TimelineStart);
        Assert.Equal(MediaTime.FromFrame(collapsedAt + 1, to), clip.TimelineEnd);
        f.AssertValid();
    }

    [Fact]
    public void CollapsingClip_GrowsLeft_WhenRightIsTaken()
    {
        // 30 → 24: X = 30f [2,3) = [66.7, 100) ms and Y = 30f [3,4) = [100, 133.3) ms.
        // Edges round to 24f 2, 2, 3: X collapses at frame 2, Y starts at 2 (no room to
        // the right), nothing before X (room to the left) → X becomes 24f [1, 2).
        var from = FrameRate.Fps30;
        var to = FrameRate.Fps24;
        var f = new TimelineFixture();
        f.Settings.FrameRate = from;
        var img = f.Image();

        var x = f.PlaceImageFrames(f.V1, img, 2, 3, from);
        var y = f.PlaceImageFrames(f.V1, img, 3, 4, from);

        Assert.Null(Regrid(f, to));

        AssertLayout(f, f.V1, new Clip[] { x, y });
        Assert.Equal(MediaTime.FromFrame(1, to), x.TimelineStart);
        Assert.Equal(MediaTime.FromFrame(2, to), x.TimelineEnd);
        Assert.Equal(MediaTime.FromFrame(2, to), y.TimelineStart);
    }

    [Fact]
    public void SandwichedCollapse_FailsAtomically()
    {
        // Four adjacent 1-frame clips at 30 FPS round to 24-FPS frames 0,1,2,2,3: the third
        // collapses between its neighbours with no room — whole operation must fail.
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Fps30;
        var img = f.Image();
        for (var i = 0; i < 4; i++)
            f.PlaceImageFrames(f.V1, img, i, i + 1, FrameRate.Fps30);
        var before = f.Snapshot();

        Assert.NotNull(Regrid(f, FrameRate.Fps24));

        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void AudioAtSourceEnd_IsShortened_NotExtendedPastSource()
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = FrameRate.Fps30;
        var audio = f.Audio(1.0); // exactly 30 frames at 30 FPS
        var clip = f.Place<AudioClip>(f.A1, audio, 0, MediaTime.FromSeconds(1).Ticks);

        Assert.Null(Regrid(f, FrameRate.Ntsc30)); // 1 s = 29.97 frames → end would round up to 30

        Assert.True(clip.SourceOut <= audio.Metadata!.Duration);
        Assert.Equal(29, clip.TimelineEnd.ToFrameFloor(FrameRate.Ntsc30));
        f.AssertValid();
    }

    [Theory]
    [MemberData(nameof(RatePairs))]
    public void RandomLayouts_EitherFailCleanly_OrProduceValidOrderedLayout(int fromNum, int fromDen, int toNum, int toDen)
    {
        var from = new FrameRate(fromNum, fromDen);
        var to = new FrameRate(toNum, toDen);
        var random = new Random(fromNum * 31 + toNum);

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var f = new TimelineFixture();
            f.Settings.FrameRate = from;
            var img = f.Image();
            var clips = new List<Clip>();
            long frame = random.Next(0, 3);
            var count = random.Next(1, 12);
            for (var i = 0; i < count; i++)
            {
                var length = random.Next(1, 6);
                clips.Add(f.PlaceImageFrames(f.V1, img, frame, frame + length, from));
                frame += length + random.Next(0, 3);
            }
            var before = f.Snapshot();
            var originalStarts = clips.Select(c => c.TimelineStart).ToList();

            var error = Regrid(f, to);

            if (error is not null)
            {
                Assert.Equal(before, f.Snapshot());
                continue;
            }

            AssertLayout(f, f.V1, clips);
            var oneTargetFrame = MediaTime.FromFrame(2, to).Ticks - MediaTime.FromFrame(0, to).Ticks; // generous bound
            for (var i = 0; i < clips.Count; i++)
                Assert.True(Math.Abs(clips[i].TimelineStart.Ticks - originalStarts[i].Ticks) <= oneTargetFrame);
        }
    }
}
