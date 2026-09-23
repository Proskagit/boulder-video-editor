using AiVideoEditor.Core.Common;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

public class SnapEngineTests
{
    private static MediaTime T(long ticks) => new(ticks);

    [Fact]
    public void PicksNearestTarget_WithinTolerance()
    {
        var result = SnapEngine.Find(new[] { T(100) }, new[] { T(0), T(95), T(120) }, T(10));
        Assert.True(result.Snapped);
        Assert.Equal(T(95), result.Target);
        Assert.Equal(T(-5), result.Offset);
    }

    [Fact]
    public void NoTargetWithinTolerance_DoesNotSnap()
    {
        Assert.False(SnapEngine.Find(new[] { T(100) }, new[] { T(0), T(200) }, T(10)).Snapped);
    }

    [Fact]
    public void BestPairAcrossCandidates_Wins()
    {
        // Clip start at 100 (target 90 → 10 away), clip end at 300 (target 302 → 2 away).
        var result = SnapEngine.Find(new[] { T(100), T(300) }, new[] { T(90), T(302) }, T(20));
        Assert.Equal(T(302), result.Target);
        Assert.Equal(T(2), result.Offset);
    }

    [Fact]
    public void Ties_AreDeterministic()
    {
        var result = SnapEngine.Find(new[] { T(100) }, new[] { T(110), T(90) }, T(10));
        Assert.Equal(T(90), result.Target); // earlier target on equal distance
    }

    [Fact]
    public void Service_ExcludesDraggedClips_AndIncludesPlayheadAndZero()
    {
        var f = new TimelineFixture();
        f.Service.AddClip(f.Image().Id);                                    // [0, 5 s)
        f.Service.AddClip(f.Image("b.png").Id, f.V1.Id, MediaTime.FromSeconds(8));
        var dragged = f.V1.Clips[1];
        f.Project.Timeline.PlayheadPosition = MediaTime.FromSeconds(6);

        var tolerance = MediaTime.FromSeconds(0.3);
        var toEdge = f.Service.Snap(new[] { MediaTime.FromSeconds(5.2) }, tolerance, TimelineFixture.Ids(dragged));
        Assert.Equal(MediaTime.FromSeconds(5), toEdge.Target);

        var toPlayhead = f.Service.Snap(new[] { MediaTime.FromSeconds(6.1) }, tolerance, TimelineFixture.Ids(dragged));
        Assert.Equal(MediaTime.FromSeconds(6), toPlayhead.Target);

        var ownEdge = f.Service.Snap(new[] { MediaTime.FromSeconds(8.1) }, tolerance, TimelineFixture.Ids(dragged));
        Assert.False(ownEdge.Snapped);
    }
}
