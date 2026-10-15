using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Vision.Tests;

/// <summary>
/// Phase 19 Step 19.4: SAM 2's pre- and postprocessing in C# (its agreement with the Python reference on real frames is
/// the local suite's; these pin the rules): the antialiased weights normalized and centred, a plain copy at the same
/// size, the ImageNet normalization in RGB order, the bilinear upsample with align_corners = False and the threshold 0.
/// </summary>
public sealed class SamImageProcessingTests
{
    [Theory]
    [InlineData(3840, 1024)]
    [InlineData(2160, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(512, 1024)]
    public void Weights_sum_to_one_and_stay_inside_the_input(int input, int output)
    {
        var weights = SamImageProcessing.Weights(input, output);
        Assert.Equal(output, weights.Length);
        foreach (var (start, w) in weights)
        {
            Assert.InRange(start, 0, input - 1);
            Assert.True(start + w.Length <= input);
            Assert.Equal(1.0, w.Sum(), 12);
            Assert.All(w, x => Assert.True(x >= 0));
        }
    }

    [Fact]
    public void At_the_same_size_each_output_takes_exactly_its_own_input()
    {
        var weights = SamImageProcessing.Weights(8, 8);
        for (var i = 0; i < 8; i++)
        {
            var (start, w) = weights[i];
            Assert.Equal(1.0, w[i - start], 12);
        }
    }

    [Fact]
    public void Downscaling_by_two_is_a_symmetric_triangle_over_the_footprint()
    {
        // scale 2: support 2, centre 1 for output 0 → taps 0..2 weighted 1 − |j + 0.5 − 1| / 2 = 0.75, 0.75, 0.25 → /1.75.
        var (start, w) = SamImageProcessing.Weights(8, 4)[0];
        Assert.Equal(0, start);
        Assert.Equal(new[] { 0.75 / 1.75, 0.75 / 1.75, 0.25 / 1.75 }.Select(x => Math.Round(x, 12)), w.Select(x => Math.Round(x, 12)));
        var (s1, w1) = SamImageProcessing.Weights(8, 4)[1];   // centre 3: taps 1..4 → 0.25, 0.75, 0.75, 0.25
        Assert.Equal(1, s1);
        Assert.Equal(new[] { 0.125, 0.375, 0.375, 0.125 }, w1.Select(x => Math.Round(x, 12)).ToArray());
    }

    [Fact]
    public void A_uniform_frame_becomes_its_normalized_rgb_colour()
    {
        const int w = 40, h = 30;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (51, 102, 204, 255);   // BGRA
        var tensor = SamImageProcessing.Preprocess(new DecodedFrame(w, h, w * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25))), 16);
        Assert.Equal(3 * 16 * 16, tensor.Length);
        Assert.All(tensor.Take(256), v => Assert.Equal((204 / 255.0 - 0.485) / 0.229, v, 5));          // R
        Assert.All(tensor.Skip(256).Take(256), v => Assert.Equal((102 / 255.0 - 0.456) / 0.224, v, 5)); // G
        Assert.All(tensor.Skip(512), v => Assert.Equal((51 / 255.0 - 0.406) / 0.225, v, 5));            // B
    }

    [Fact]
    public void The_upsample_is_bilinear_with_align_corners_false_and_threshold_zero()
    {
        // 2 × 2 logits: -1 left, +1 right; to 4 × 4 the columns sit at src = (x + 0.5)·0.5 − 0.5 = -0.25→0, 0.25, 0.75,
        // 1.25→1: values -1, -0.5, +0.5, +1 → the right two columns are inside.
        var mask = SamImageProcessing.Upsample(new float[] { -1, 1, -1, 1 }, 2, 4, 4);
        for (var y = 0; y < 4; y++)
            Assert.Equal(new byte[] { 0, 0, 1, 1 }, mask.Skip(y * 4).Take(4).ToArray());

        Assert.All(SamImageProcessing.Upsample(new float[] { -1, -1, -1, -1 }, 2, 8, 8), v => Assert.Equal(0, v));
        Assert.All(SamImageProcessing.Upsample(new float[] { 0, 0, 0, 0 }, 2, 8, 8), v => Assert.Equal(0, v));   // > 0, not ≥ 0
    }
}
