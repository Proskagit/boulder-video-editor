using System.Numerics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Property tests: after random move / trim / split / delete / undo / redo sequences made
/// through the real <see cref="TimelineEditService"/>, every timeline frame of every clip
/// selects the same source frame as the exact rational rule (D009) evaluated without ticks.
/// This proves tick rounding of clip edges and SourceIn never flips a frame choice.
/// </summary>
public class SourceFrameSelectionPropertyTests
{
    private const double SourceSeconds = 20;

    public static TheoryData<FrameRate, FrameRate, bool> Cases => new()
    {
        // source, project, millisecond (MKV-style) timestamps
        { FrameRate.Ntsc30, FrameRate.Ntsc30, false },
        { FrameRate.Ntsc30, FrameRate.Ntsc30, true },
        { FrameRate.Ntsc24, FrameRate.Ntsc24, true },
        { FrameRate.Fps25, FrameRate.Fps25, false },
        { FrameRate.Fps24, FrameRate.Fps24, false },
        { FrameRate.Fps25, FrameRate.Ntsc30, false },
        { FrameRate.Fps24, FrameRate.Ntsc30, false },
        { FrameRate.Ntsc30, FrameRate.Fps24, false },
        { FrameRate.Ntsc60, FrameRate.Ntsc30, false },
        { FrameRate.Ntsc60, FrameRate.Ntsc30, true },
        { FrameRate.Fps60, FrameRate.Fps30, false },
        { FrameRate.Fps25, new FrameRate(50, 1), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomEdits_EveryFrameMatchesExactRule(FrameRate sourceRate, FrameRate projectRate, bool millisecondPts)
    {
        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
            RunScenario(sourceRate, projectRate, millisecondPts, seed);
    }

    private static void RunScenario(FrameRate sourceRate, FrameRate projectRate, bool millisecondPts, int seed)
    {
        var f = new TimelineFixture();

        // First video fixes the project rate (D007); remove it so only the source under test remains.
        var locking = f.Video(SourceSeconds, projectRate, "lock.mp4");
        Assert.True(f.Service.AddClip(locking.Id).Success);
        Assert.Equal(projectRate, f.Rate);
        Assert.True(f.Service.DeleteClips(TimelineFixture.Ids(f.V1.Clips[0])).Success);
        f.UndoRedo.Clear(); // random undos must not reach back past the rate lock

        var asset = f.Video(SourceSeconds, sourceRate, "source.mp4");
        asset.Metadata!.AvgFrameRate = sourceRate;
        var frames = SourceFrames(sourceRate, millisecondPts);

        var random = new Random(seed);
        for (var i = 0; i < 3; i++)
            Assert.True(f.Service.AddClip(asset.Id).Success);

        for (var step = 0; step < 60; step++)
        {
            ApplyRandomEdit(f, asset, random);
            f.AssertValid();
            AssertAllFramesMatch(f, asset, frames, sourceRate, projectRate);
        }
    }

    private static void ApplyRandomEdit(TimelineFixture f, MediaAsset asset, Random random)
    {
        var clips = f.V1.Clips.OfType<VideoClip>().ToList();
        var clip = clips.Count > 0 ? clips[random.Next(clips.Count)] : null;
        var rate = f.Rate;

        switch (random.Next(8))
        {
            case 0 when clip is not null:
                f.Service.MoveClips(TimelineFixture.Ids(clip), random.Next(-45, 46));
                break;
            case 1 when clip is not null:
                f.Service.TrimClip(clip.Id, ClipEdge.Start, clip.TimelineStart + Frames(random.Next(-40, 41), rate));
                break;
            case 2 when clip is not null:
                f.Service.TrimClip(clip.Id, ClipEdge.End, clip.TimelineEnd + Frames(random.Next(-40, 41), rate));
                break;
            case 3 when clip is not null:
                var first = clip.TimelineStart.ToFrameFloor(rate);
                var last = clip.TimelineEnd.ToFrameFloor(rate);
                if (last - first >= 2)
                    f.Service.Split(MediaTime.FromFrame(random.NextInt64(first + 1, last), rate), TimelineFixture.Ids(clip));
                break;
            case 4 when clips.Count > 2:
                f.Service.DeleteClips(TimelineFixture.Ids(clip!));
                break;
            case 5:
                f.Service.AddClip(asset.Id);
                break;
            case 6:
                f.UndoRedo.Undo();
                break;
            default:
                f.UndoRedo.Redo();
                break;
        }
    }

    /// <summary>Signed frame offset as a time delta, via the grid (not k·duration).</summary>
    private static MediaTime Frames(long count, FrameRate rate)
    {
        var span = MediaTime.FromFrame(Math.Abs(count), rate);
        return count < 0 ? MediaTime.Zero - span : span;
    }

    private static List<SourceTimestamp> SourceFrames(FrameRate rate, bool millisecondPts)
    {
        var count = (int)((long)SourceSeconds * rate.Numerator / rate.Denominator) + 1;
        return millisecondPts
            ? Enumerable.Range(0, count)
                .Select(i => new SourceTimestamp((2L * i * 1000 * rate.Denominator + rate.Numerator) / (2L * rate.Numerator), new TimeBase(1, 1000)))
                .ToList()
            : Enumerable.Range(0, count)
                .Select(i => new SourceTimestamp((long)i * rate.Denominator, new TimeBase(1, rate.Numerator)))
                .ToList();
    }

    private static void AssertAllFramesMatch(TimelineFixture f, MediaAsset asset, List<SourceTimestamp> frames,
        FrameRate sourceRate, FrameRate projectRate)
    {
        foreach (var clip in f.V1.Clips.OfType<VideoClip>())
        {
            // The clip's intended source offset in whole project frames. Edits keep
            // SourceIn − TimelineStart within a couple of ticks of an exact frame multiple.
            var offsetTicks = clip.SourceIn.Ticks - clip.TimelineStart.Ticks;
            var offsetFrames = (long)Math.Round((double)offsetTicks * projectRate.Numerator / (TimeSpan.TicksPerSecond * (double)projectRate.Denominator));
            var exactOffsetTicks = (double)offsetFrames * TimeSpan.TicksPerSecond * projectRate.Denominator / projectRate.Numerator;
            Assert.True(Math.Abs(offsetTicks - exactOffsetTicks) < 3, $"SourceIn drifted: {offsetTicks} vs {exactOffsetTicks}");

            var first = clip.TimelineStart.ToFrameFloor(projectRate);
            var end = clip.TimelineEnd.ToFrameFloor(projectRate);
            for (var n = first; n < end; n++)
            {
                var point = SourceFrameSelector.SamplePoint(clip, n, projectRate, asset.Metadata);
                var actual = SourceFrameSelector.Select(frames, MediaTime.Zero, point);
                var expected = Math.Min(ExactIndex(n + offsetFrames, projectRate, sourceRate), frames.Count - 1);
                Assert.True(expected == actual,
                    $"clip {clip.Id} timeline frame {n}: expected source frame {expected}, got {actual} ({sourceRate} in {projectRate})");
            }
        }
    }

    /// <summary>floor((m/Rp + ½·min(1/Rp, 1/Rs)) · Rs) in exact BigInteger arithmetic.</summary>
    private static long ExactIndex(long m, FrameRate project, FrameRate source)
    {
        BigInteger pN = project.Numerator, pD = project.Denominator, sN = source.Numerator, sD = source.Denominator;
        var point = 2 * m * pD * sN + BigInteger.Min(pD * sN, sD * pN);
        return (long)BigInteger.Divide(point * sN, 2 * pN * sN * sD);
    }
}
