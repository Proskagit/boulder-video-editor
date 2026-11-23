using AiVideoEditor.Core.People;
using AiVideoEditor.Vision.People;
using Xunit;

namespace AiVideoEditor.Vision.Tests;

/// <summary>D078 §3: the pose's 17 keypoints decoded from SimCC outputs into normalized frame coordinates.</summary>
public sealed class PersonKeypointsTests
{
    [Fact]
    public void Every_keypoint_lands_where_its_arg_max_points_in_the_frame()
    {
        const int wx = PeopleModel.PoseWidth * 2, wy = PeopleModel.PoseHeight * 2;
        var x = new float[17 * wx];
        var y = new float[17 * wy];
        for (var k = 0; k < 17; k++)
        {
            x[k * wx + 2 * (10 + k * 5)] = 0.9f;      // pose pixel 10 + 5k
            y[k * wy + 2 * (20 + k * 10)] = k == 3 ? 0.1f : 0.8f;
        }
        // A crop of 192 × 256 frame pixels centred at (500, 400): pose pixel p maps to 404 + p, 272 + p.
        var points = PersonDetectorMath.Keypoints(x, wx, y, wy, (500, 400, 192, 256), 1000, 800);

        Assert.Equal(51, points.Length);
        var person = new PersonDetection(new NormalizedBox(0, 0, 1, 1), 1, null, default) { Keypoints = points };
        for (var k = 0; k < 17; k++)
        {
            Assert.Equal((404 + 10 + k * 5) / 1000.0, points[k * 3], 4);
            Assert.Equal((272 + 20 + k * 10) / 800.0, points[k * 3 + 1], 4);
            Assert.Equal(k == 3 ? 0.1f : 0.8f, points[k * 3 + 2]);
        }
        Assert.Null(person.KeypointAt(3));                       // below the pose's threshold
        Assert.NotNull(person.KeypointAt(CocoKeypoint.LeftAnkle));
        Assert.Null(new PersonDetection(new NormalizedBox(0, 0, 1, 1), 1, null, default).KeypointAt(0));   // no pose
    }
}
