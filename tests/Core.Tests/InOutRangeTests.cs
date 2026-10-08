using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 15 Step 15.7 (D030 §8, Q7 / Q8): the In / Out rules. In = the start of the frame containing the playhead; Out =
/// the end of the frame containing the playhead, so that frame is in <c>[In, Out)</c>; a point that would leave In ≥ Out
/// keeps the new point and clears the other; only In runs to the end of the sequence, only Out from 0; the range is
/// clamped to the sequence; a frame-rate change keeps the points' times on the new grid.
/// </summary>
public class InOutRangeTests
{
    private static readonly FrameRate Rate = FrameRate.Ntsc30;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static readonly MediaTime Sequence = F(300);

    [Fact]
    public void I_sets_In_at_the_start_of_the_frame_containing_the_playhead()
    {
        var range = InOutRange.None.WithIn(F(40), Rate);
        Assert.Equal((F(40), (MediaTime?)null), (range.In!.Value, range.Out));

        var between = InOutRange.None.WithIn(F(40) + new MediaTime(100), Rate);         // inside frame 40
        Assert.Equal(F(40), between.In);
    }

    [Fact]
    public void O_includes_the_frame_under_the_playhead_the_boundary_is_its_end()
    {
        var range = InOutRange.None.WithOut(F(40), Rate);
        Assert.Equal(F(41), range.Out);                                                  // exclusive: frame 40 is in
        Assert.Equal((0L, 41L), range.Frames(Rate, Sequence));
        Assert.Equal(F(41), InOutRange.None.WithOut(F(41) - new MediaTime(1), Rate).Out);
    }

    [Fact]
    public void I_and_O_on_one_frame_give_a_one_frame_range()
    {
        var range = InOutRange.None.WithIn(F(77), Rate).WithOut(F(77), Rate);
        Assert.Equal((77L, 78L), range.Frames(Rate, Sequence));
        Assert.Equal((77L, 78L), InOutRange.None.WithOut(F(77), Rate).WithIn(F(77), Rate).Frames(Rate, Sequence));
    }

    [Fact]
    public void A_point_that_would_invert_the_range_keeps_itself_and_clears_the_other()
    {
        var range = InOutRange.None.WithIn(F(50), Rate).WithOut(F(100), Rate);       // [50, 101)

        var laterIn = range.WithIn(F(120), Rate);                                        // In after Out
        Assert.Equal((F(120), (MediaTime?)null), (laterIn.In!.Value, laterIn.Out));
        var inAtOut = range.WithIn(F(101), Rate);                                        // In exactly at Out
        Assert.Equal((F(101), (MediaTime?)null), (inAtOut.In!.Value, inAtOut.Out));
        var inOnLastFrame = range.WithIn(F(100), Rate);                                  // still valid: one frame
        Assert.Equal((100L, 101L), inOnLastFrame.Frames(Rate, Sequence));

        var earlierOut = range.WithOut(F(30), Rate);                                     // Out before In
        Assert.Equal(((MediaTime?)null, F(31)), (earlierOut.In, earlierOut.Out!.Value));
        var outOnIn = range.WithOut(F(50), Rate);                                        // Out on In's frame: valid
        Assert.Equal((50L, 51L), outOnIn.Frames(Rate, Sequence));
        var outBeforeInFrame = range.WithOut(F(49), Rate);                               // ends where In starts: clears In
        Assert.Null(outBeforeInFrame.In);
    }

    [Fact]
    public void Only_In_runs_to_the_end_only_Out_from_0_and_the_range_is_clamped_to_the_sequence()
    {
        Assert.Equal((40L, 300L), InOutRange.None.WithIn(F(40), Rate).Frames(Rate, Sequence));
        Assert.Equal((0L, 61L), InOutRange.None.WithOut(F(60), Rate).Frames(Rate, Sequence));
        Assert.Equal((250L, 300L), InOutRange.None.WithIn(F(250), Rate).WithOut(F(400), Rate).Frames(Rate, Sequence));
        Assert.Null(InOutRange.None.WithIn(F(300), Rate).Frames(Rate, Sequence));       // nothing of the sequence inside
        Assert.Null(InOutRange.None.Frames(Rate, Sequence));
        Assert.False(InOutRange.None.IsSet);
        Assert.Null(InOutRange.None.WithIn(F(0), Rate).Frames(Rate, MediaTime.Zero));    // an empty sequence
    }

    [Fact]
    public void A_frame_rate_change_keeps_the_times_on_the_nearest_frames_of_the_new_grid()
    {
        var range = InOutRange.None.WithIn(F(30), Rate).WithOut(F(59), Rate);         // [1.001 s, 2.002 s)
        var regridded = range.Regrid(FrameRate.Fps25);
        Assert.Equal(MediaTime.FromFrame(25, FrameRate.Fps25), regridded.In);          // 1.001 s → frame 25
        Assert.Equal(MediaTime.FromFrame(50, FrameRate.Fps25), regridded.Out);         // 2.002 s → frame 50

        var tight = InOutRange.None.WithIn(F(30), Rate).WithOut(F(30), Rate);          // one 29.97 frame
        var collapsed = tight.Regrid(new FrameRate(1, 1));                                  // both on the same 1 fps frame
        Assert.NotNull(collapsed.In);
        Assert.Null(collapsed.Out);
    }
}
