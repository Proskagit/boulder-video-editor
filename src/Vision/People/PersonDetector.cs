using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiVideoEditor.Vision.People;

/// <summary>
/// RF-DETR-M person detection and RTMPose-m torso centres on one frame (D061 §3.2), with the pre- and postprocessing of
/// their reference implementations (rtmlib 0.0.16): the frame stretched to 576 × 576 (bilinear), RGB / 255 normalized
/// with the ImageNet mean and deviation; class 1 ("person", index 0 is the background) through a sigmoid; boxes
/// (cx, cy, w, h) already normalized. The pose crop: the box enlarged 1.25×, at the model's aspect ratio, warped to
/// 192 × 256, RGB normalized by the mmpose mean / deviation; SimCC decoded by the arg-max (split ratio 2).
/// </summary>
internal static class PersonDetectorMath
{
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };
    private static readonly float[] PoseMean = { 123.675f, 116.28f, 103.53f };
    private static readonly float[] PoseStd = { 58.395f, 57.12f, 57.375f };

    /// <summary>COCO keypoints of the torso: left / right shoulder, left / right hip.</summary>
    private static readonly int[] Torso = { 5, 6, 11, 12 };

    public const float KeypointThreshold = 0.3f;

    /// <summary>The detector input <c>[1, 3, 576, 576]</c>: plain bilinear (cv2.INTER_LINEAR, align corners false).</summary>
    public static float[] DetectorInput(DecodedFrame frame)
    {
        const int size = PeopleModel.DetectorInput;
        var tensor = new float[3 * size * size];
        var pixels = frame.Pixels.Span;
        var xs = Taps(frame.Width, size);
        var ys = Taps(frame.Height, size);
        for (var y = 0; y < size; y++)
        {
            var (y0, y1, fy) = ys[y];
            for (var x = 0; x < size; x++)
            {
                var (x0, x1, fx) = xs[x];
                for (var c = 0; c < 3; c++)
                {
                    var channel = 2 - c;   // BGRA → R, G, B
                    double v00 = pixels[y0 * frame.Stride + x0 * 4 + channel], v01 = pixels[y0 * frame.Stride + x1 * 4 + channel];
                    double v10 = pixels[y1 * frame.Stride + x0 * 4 + channel], v11 = pixels[y1 * frame.Stride + x1 * 4 + channel];
                    var v = (v00 * (1 - fx) + v01 * fx) * (1 - fy) + (v10 * (1 - fx) + v11 * fx) * fy;
                    tensor[c * size * size + y * size + x] = (float)((v / 255.0 - ImageNetMean[c]) / ImageNetStd[c]);
                }
            }
        }
        return tensor;
    }

    /// <summary>The people among the detector's outputs (<c>dets [1, Q, 4]</c> normalized cx, cy, w, h; <c>labels [1, Q, C]</c>
    /// logits): class 1 above <paramref name="threshold"/>, boxes clamped to the frame.</summary>
    public static List<(NormalizedBox Box, float Score)> People(ReadOnlySpan<float> dets, ReadOnlySpan<float> labels, int queries, int classes, float threshold)
    {
        var result = new List<(NormalizedBox, float)>();
        for (var q = 0; q < queries; q++)
        {
            var score = (float)(1 / (1 + Math.Exp(-labels[q * classes + 1])));
            if (score < threshold) continue;
            double cx = dets[q * 4], cy = dets[q * 4 + 1], w = Math.Max(0, dets[q * 4 + 2]), h = Math.Max(0, dets[q * 4 + 3]);
            var box = new NormalizedBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2).Clamped();
            if (box.Width <= 0 || box.Height <= 0) continue;
            result.Add((box, score));
        }
        return result;
    }

    /// <summary>The pose crop of <paramref name="box"/>: its centre and size in frame pixels (1.25× the box, at 192 : 256) and
    /// the input <c>[1, 3, 256, 192]</c>.</summary>
    public static (double Cx, double Cy, double W, double H, float[] Input) PoseInput(DecodedFrame frame, NormalizedBox box)
    {
        double cx = (box.X0 + box.X1) / 2 * frame.Width, cy = (box.Y0 + box.Y1) / 2 * frame.Height;
        double w = box.Width * frame.Width * 1.25, h = box.Height * frame.Height * 1.25;
        const double aspect = (double)PeopleModel.PoseWidth / PeopleModel.PoseHeight;
        if (w > h * aspect) h = w / aspect; else w = h * aspect;
        const int ow = PeopleModel.PoseWidth, oh = PeopleModel.PoseHeight;
        var tensor = new float[3 * ow * oh];
        var pixels = frame.Pixels.Span;
        for (var y = 0; y < oh; y++)
        {
            var sy = cy - h / 2 + (y + 0.5) * h / oh - 0.5;
            for (var x = 0; x < ow; x++)
            {
                var sx = cx - w / 2 + (x + 0.5) * w / ow - 0.5;
                for (var c = 0; c < 3; c++)
                {
                    var v = Sample(pixels, frame, sx, sy, 2 - c);
                    tensor[c * ow * oh + y * ow + x] = (float)((v - PoseMean[c]) / PoseStd[c]);
                }
            }
        }
        return (cx, cy, w, h, tensor);
    }

    /// <summary>The torso centre from SimCC outputs (<c>[17, Wx]</c>, <c>[17, Wy]</c>) in normalized frame coordinates; null
    /// when fewer than two of the four torso keypoints, or none of the shoulders, are confident.</summary>
    public static NormalizedPoint? TorsoCentre(ReadOnlySpan<float> simccX, int widthX, ReadOnlySpan<float> simccY, int widthY,
        (double Cx, double Cy, double W, double H) crop, int frameWidth, int frameHeight)
    {
        double sx = 0, sy = 0;
        int n = 0, shoulders = 0;
        foreach (var k in Torso)
        {
            var (ix, vx) = ArgMax(simccX.Slice(k * widthX, widthX));
            var (iy, vy) = ArgMax(simccY.Slice(k * widthY, widthY));
            var score = Math.Min(vx, vy);
            if (score < KeypointThreshold) continue;
            var px = ix / 2.0 / PeopleModel.PoseWidth * crop.W + crop.Cx - crop.W / 2;
            var py = iy / 2.0 / PeopleModel.PoseHeight * crop.H + crop.Cy - crop.H / 2;
            sx += px; sy += py; n++;
            if (k is 5 or 6) shoulders++;
        }
        if (n < 2 || shoulders == 0) return null;
        return new NormalizedPoint(Math.Clamp(sx / n / frameWidth, 0, 1), Math.Clamp(sy / n / frameHeight, 0, 1));
    }

    /// <summary>D078 §3: all 17 keypoints from SimCC outputs — x, y in normalized frame coordinates (clamped to 0..1) and the
    /// score (the lower of the two axes' maxima), 51 values.</summary>
    public static ImmutableArray<float> Keypoints(ReadOnlySpan<float> simccX, int widthX, ReadOnlySpan<float> simccY, int widthY,
        (double Cx, double Cy, double W, double H) crop, int frameWidth, int frameHeight)
    {
        var values = new float[PersonDetection.KeypointCount * 3];
        for (var k = 0; k < PersonDetection.KeypointCount; k++)
        {
            var (ix, vx) = ArgMax(simccX.Slice(k * widthX, widthX));
            var (iy, vy) = ArgMax(simccY.Slice(k * widthY, widthY));
            var px = ix / 2.0 / PeopleModel.PoseWidth * crop.W + crop.Cx - crop.W / 2;
            var py = iy / 2.0 / PeopleModel.PoseHeight * crop.H + crop.Cy - crop.H / 2;
            values[k * 3] = (float)Math.Clamp(px / frameWidth, 0, 1);
            values[k * 3 + 1] = (float)Math.Clamp(py / frameHeight, 0, 1);
            values[k * 3 + 2] = Math.Min(vx, vy);
        }
        return ImmutableArray.Create(values);
    }

    private static (int Index, float Value) ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
        return (best, values[best]);
    }

    private static double Sample(ReadOnlySpan<byte> pixels, DecodedFrame frame, double x, double y, int channel)
    {
        if (x < -0.5 || y < -0.5 || x > frame.Width - 0.5 || y > frame.Height - 0.5) return 0;   // outside: black (cv2 border 0)
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var fx = x - x0; var fy = y - y0;
        int xa = Math.Clamp(x0, 0, frame.Width - 1), xb = Math.Clamp(x0 + 1, 0, frame.Width - 1);
        int ya = Math.Clamp(y0, 0, frame.Height - 1), yb = Math.Clamp(y0 + 1, 0, frame.Height - 1);
        double p00 = pixels[ya * frame.Stride + xa * 4 + channel], p01 = pixels[ya * frame.Stride + xb * 4 + channel];
        double p10 = pixels[yb * frame.Stride + xa * 4 + channel], p11 = pixels[yb * frame.Stride + xb * 4 + channel];
        return (p00 * (1 - fx) + p01 * fx) * (1 - fy) + (p10 * (1 - fx) + p11 * fx) * fy;
    }

    /// <summary>Bilinear taps (align corners false), clamped at the edges.</summary>
    internal static (int I0, int I1, double F)[] Taps(int input, int output)
    {
        var scale = (double)input / output;
        var taps = new (int, int, double)[output];
        for (var i = 0; i < output; i++)
        {
            var src = Math.Max((i + 0.5) * scale - 0.5, 0);
            var i0 = Math.Min((int)src, input - 1);
            var i1 = Math.Min(i0 + 1, input - 1);
            taps[i] = (i0, i1, src - i0);
        }
        return taps;
    }
}

/// <summary>The detector and pose sessions on one device.</summary>
internal sealed class PersonDetectorSessions : IDisposable
{
    private readonly InferenceSession _detector;
    private readonly InferenceSession _pose;

    public PersonDetectorSessions(string detectorPath, string posePath, Core.Interfaces.InferenceDevice device)
    {
        Device = device;
        _detector = new InferenceSession(detectorPath, OnnxSessions.Options(device));
        try
        {
            _pose = new InferenceSession(posePath, OnnxSessions.Options(device));
        }
        catch
        {
            _detector.Dispose();
            throw;
        }
    }

    public Core.Interfaces.InferenceDevice Device { get; }

    public IReadOnlyList<PersonDetection> Detect(DecodedFrame frame, CancellationToken cancellationToken)
    {
        var input = new DenseTensor<float>(PersonDetectorMath.DetectorInput(frame), new[] { 1, 3, PeopleModel.DetectorInput, PeopleModel.DetectorInput });
        List<(NormalizedBox Box, float Score)> found;
        using (var results = OnnxSessions.Run(_detector, new[] { NamedOnnxValue.CreateFromTensor("input", input) }, cancellationToken))
        {
            var dets = results.First(r => r.Name == "dets").AsTensor<float>();
            var labels = results.First(r => r.Name == "labels").AsTensor<float>();
            found = PersonDetectorMath.People(((DenseTensor<float>)dets).Buffer.Span, ((DenseTensor<float>)labels).Buffer.Span,
                dets.Dimensions[1], labels.Dimensions[2], PersonTracker.LowScore);
        }
        var people = PersonTracker.Suppress(found.Select(f => new PersonDetection(f.Box, f.Score, null, ImmutableArray<float>.Empty)));

        var result = new List<PersonDetection>(people.Length);
        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizedPoint? torso = null;
            var keypoints = ImmutableArray<float>.Empty;
            if (person.Score >= PersonTracker.LowScore && person.Box.Width * frame.Width >= 8 && person.Box.Height * frame.Height >= 8)
            {
                var crop = PersonDetectorMath.PoseInput(frame, person.Box);
                var tensor = new DenseTensor<float>(crop.Input, new[] { 1, 3, PeopleModel.PoseHeight, PeopleModel.PoseWidth });
                using var results = OnnxSessions.Run(_pose, new[] { NamedOnnxValue.CreateFromTensor("input", tensor) }, cancellationToken);
                var x = (DenseTensor<float>)results.First(r => r.Name == "simcc_x").AsTensor<float>();
                var y = (DenseTensor<float>)results.First(r => r.Name == "simcc_y").AsTensor<float>();
                torso = PersonDetectorMath.TorsoCentre(x.Buffer.Span, x.Dimensions[2], y.Buffer.Span, y.Dimensions[2],
                    (crop.Cx, crop.Cy, crop.W, crop.H), frame.Width, frame.Height);
                keypoints = PersonDetectorMath.Keypoints(x.Buffer.Span, x.Dimensions[2], y.Buffer.Span, y.Dimensions[2],
                    (crop.Cx, crop.Cy, crop.W, crop.H), frame.Width, frame.Height);
                // A torso outside the person's own box is a confused pose: the box centre is safer.
                if (torso is { } t && !person.Box.Contains(t)) torso = null;
            }
            result.Add(person with { Torso = torso, Appearance = PersonAppearance.Of(frame, person.Box), Keypoints = keypoints });
        }
        return result;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _pose.Dispose();
    }
}

/// <summary>Session options and runs shared by the people models (D034 §3's DirectML settings).</summary>
internal static class OnnxSessions
{
    public static SessionOptions Options(Core.Interfaces.InferenceDevice device)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (device == Core.Interfaces.InferenceDevice.Gpu)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        return options;
    }

    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        try
        {
            return session.Run(inputs, session.OutputNames, runOptions);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
