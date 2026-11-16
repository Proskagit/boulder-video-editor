using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiVideoEditor.Vision.People;

/// <summary>The six SAM 2.1 tiny video graphs and the constants, on one device (D061 §4).</summary>
internal sealed class Sam2Sessions : IDisposable
{
    public const int NumMaskMem = 7;
    public const int MaxPointers = 16;

    public Sam2Sessions(Func<HoldModelFile, string> path, InferenceDevice device)
    {
        var sessions = new List<InferenceSession>();
        InferenceSession Open(HoldModelFile file)
        {
            var s = new InferenceSession(path(file), OnnxSessions.Options(device));
            sessions.Add(s);
            return s;
        }
        try
        {
            Encoder = Open(PeopleModel.SamEncoder);
            MemoryAttention = Open(PeopleModel.SamMemoryAttention);
            DecoderMulti = Open(PeopleModel.SamDecoderMulti);
            DecoderSingle = Open(PeopleModel.SamDecoderSingle);
            MemoryEncoder = Open(PeopleModel.SamMemoryEncoder);
            PointerPos = Open(PeopleModel.SamPointerPos);
        }
        catch
        {
            foreach (var s in sessions) s.Dispose();
            throw;
        }
        (NoMemEmbed, MaskMemTpos) = ReadConstants(path(PeopleModel.SamConstants));
    }

    public InferenceSession Encoder { get; }
    public InferenceSession MemoryAttention { get; }
    public InferenceSession DecoderMulti { get; }
    public InferenceSession DecoderSingle { get; }
    public InferenceSession MemoryEncoder { get; }
    public InferenceSession PointerPos { get; }

    /// <summary><c>no_mem_embed</c> [256] and <c>maskmem_tpos_enc</c> [7 × 64].</summary>
    public float[] NoMemEmbed { get; }
    public float[] MaskMemTpos { get; }

    internal static (float[] NoMem, float[] Tpos) ReadConstants(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || System.Text.Encoding.ASCII.GetString(bytes, 0, 8) != "SAM2CST1")
            throw new InvalidDataException("The SAM 2 constants file has an unknown format.");
        var arrays = new List<float[]>();
        var offset = 8;
        while (offset + 4 <= bytes.Length)
        {
            var n = BitConverter.ToInt32(bytes, offset);
            offset += 4;
            var a = new float[n];
            Buffer.BlockCopy(bytes, offset, a, 0, n * 4);
            offset += n * 4;
            arrays.Add(a);
        }
        if (arrays.Count < 2 || arrays[0].Length != 256 || arrays[1].Length != NumMaskMem * 64)
            throw new InvalidDataException("The SAM 2 constants file is incomplete.");
        return (arrays[0], arrays[1]);
    }

    public void Dispose()
    {
        Encoder.Dispose();
        MemoryAttention.Dispose();
        DecoderMulti.Dispose();
        DecoderSingle.Dispose();
        MemoryEncoder.Dispose();
        PointerPos.Dispose();
    }
}

/// <summary>
/// SAM 2.1's video mode as a streaming loop (D061 §4) — the port of <c>tools/people-models/validate_sam2_video.py</c>,
/// which matches the official PyTorch predictor (mean IoU 0.995 on the validation clip). Per person: the prompted
/// ("conditioning") frames and the last six frames as spatial memories with their temporal encodings, the object pointers
/// of up to 16 frames with their signed temporal distance; a person is started with its detector box (a single-mask
/// decoding, the mask binarized for the memory) and then tracked without prompts (three masks, the best by IoU). The memory
/// is bounded: frames older than 16 steps are forgotten, at most <see cref="MaxConditioning"/> prompted frames are kept.
/// The frame features are computed once per frame for all people.
/// </summary>
internal sealed class Sam2VideoSegmenter : IPersonSegmenter
{
    /// <summary>The prompted frames a person keeps in its memory (the latest ones).</summary>
    public const int MaxConditioning = 3;

    /// <summary>A frame's memory: the spatial features and their encoding, sequence first ([4096, 64] — transposed once when
    /// the memory is made, not on every frame that attends to it; kept only while the frame is among the last six —
    /// <see cref="Forget"/>) and the object pointer (kept for sixteen).</summary>
    private sealed record FrameMemory(float[]? Features, float[]? Position, float[] Pointer);

    /// <summary>[64, 4096] (channels first, as the memory encoder gives it) → [4096, 64] (sequence first).</summary>
    private static float[] SequenceFirst(float[] channelsFirst)
    {
        var result = new float[4096 * 64];
        for (var c = 0; c < 64; c++)
        {
            var row = c * 4096;
            for (var i = 0; i < 4096; i++) result[i * 64 + c] = channelsFirst[row + i];
        }
        return result;
    }

    private sealed class Person
    {
        public readonly SortedDictionary<int, FrameMemory> Conditioning = new();
        public readonly Dictionary<int, FrameMemory> Previous = new();
    }

    private readonly Sam2Sessions _s;
    private readonly bool _ownsSessions;
    private readonly Dictionary<int, Person> _people = new();
    private int _frame = -1;

    /// <summary>Frames preprocessed ahead (<see cref="Prepare"/>, another thread) until their <see cref="Step"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DecodedFrame, float[]> _prepared = new(ReferenceEqualityComparer.Instance);

    /// <summary>The encoder's positional encoding sequence first ([4096, 1, 256]) — the same on every frame (it depends on
    /// the feature size only), so it is transposed once.</summary>
    private float[]? _currPos;

    public void Prepare(DecodedFrame frame) => _prepared[frame] = SamImageProcessing.Preprocess(frame, PeopleModel.SamInput);

    public Sam2VideoSegmenter(Sam2Sessions sessions, bool ownsSessions)
    {
        _s = sessions;
        _ownsSessions = ownsSessions;
    }

    public IReadOnlyList<SegmentedPerson> Step(DecodedFrame frame, IReadOnlyList<SegmenterPrompt> start, IReadOnlyCollection<int> stop,
        int gridWidth, int gridHeight, CancellationToken cancellationToken)
    {
        _frame++;
        foreach (var id in stop) _people.Remove(id);
        foreach (var prompt in start) _people[prompt.PersonId] = new Person();
        if (_people.Count == 0)
        {
            _prepared.TryRemove(frame, out _);
            return Array.Empty<SegmentedPerson>();
        }

        var pixels = _prepared.TryRemove(frame, out var ready) ? ready : SamImageProcessing.Preprocess(frame, PeopleModel.SamInput);
        var image = new DenseTensor<float>(pixels, new[] { 1, 3, PeopleModel.SamInput, PeopleModel.SamInput });
        float[] vf, vp, h0, h1;
        using (var enc = OnnxSessions.Run(_s.Encoder, new[] { NamedOnnxValue.CreateFromTensor("image", image) }, cancellationToken))
        {
            vf = Out(enc, "vision_feat");
            vp = Out(enc, "vision_pos");
            h0 = Out(enc, "high_res_0");
            h1 = Out(enc, "high_res_1");
        }

        var prompts = start.ToDictionary(p => p.PersonId, p => p.Box);
        var results = new List<(int Id, bool Visible, float[] Logits)>();
        foreach (var (id, person) in _people.OrderBy(p => p.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompted = prompts.TryGetValue(id, out var box);
            float[] pix;
            float[] coords;
            int[] labels;
            InferenceSession decoder;
            if (prompted)
            {
                pix = new float[vf.Length];
                for (var c = 0; c < 256; c++)
                {
                    var add = _s.NoMemEmbed[c];
                    for (var i = 0; i < 4096; i++) pix[c * 4096 + i] = vf[c * 4096 + i] + add;
                }
                const float n = PeopleModel.SamInput;
                coords = new[] { (float)box.X0 * n, (float)box.Y0 * n, (float)box.X1 * n, (float)box.Y1 * n };
                labels = new[] { 2, 3 };
                decoder = _s.DecoderSingle;
            }
            else
            {
                pix = Attend(person, vf, vp, cancellationToken);
                coords = new float[2];
                labels = new[] { -1 };
                decoder = _s.DecoderMulti;
            }

            float[] mask, pointer, score;
            using (var dec = OnnxSessions.Run(decoder, new[]
                   {
                       NamedOnnxValue.CreateFromTensor("pix_feat", new DenseTensor<float>(pix, new[] { 1, 256, 64, 64 })),
                       NamedOnnxValue.CreateFromTensor("high_res_0", new DenseTensor<float>(h0, new[] { 1, 32, 256, 256 })),
                       NamedOnnxValue.CreateFromTensor("high_res_1", new DenseTensor<float>(h1, new[] { 1, 64, 128, 128 })),
                       NamedOnnxValue.CreateFromTensor("point_coords", new DenseTensor<float>(coords, new[] { 1, labels.Length, 2 })),
                       NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<int>(labels, new[] { 1, labels.Length }))
                   }, cancellationToken))
            {
                mask = Out(dec, "low_res_mask");
                pointer = Out(dec, "obj_ptr");
                score = Out(dec, "object_score_logits");
            }

            float[] mf, mp;
            using (var menc = OnnxSessions.Run(_s.MemoryEncoder, new[]
                   {
                       NamedOnnxValue.CreateFromTensor("vision_feat", new DenseTensor<float>(vf, new[] { 1, 256, 64, 64 })),
                       NamedOnnxValue.CreateFromTensor("low_res_mask", new DenseTensor<float>(mask, new[] { 1, 1, PeopleModel.SamMask, PeopleModel.SamMask })),
                       NamedOnnxValue.CreateFromTensor("object_score_logits", new DenseTensor<float>(score, new[] { 1, 1 })),
                       NamedOnnxValue.CreateFromTensor("binarize", new DenseTensor<float>(new[] { prompted ? 1f : 0f }, new[] { 1, 1, 1, 1 }))
                   }, cancellationToken))
            {
                mf = Out(menc, "maskmem_features");
                mp = Out(menc, "maskmem_pos");
            }

            var memory = new FrameMemory(SequenceFirst(mf), SequenceFirst(mp), pointer);
            if (prompted)
            {
                person.Conditioning[_frame] = memory;
                while (person.Conditioning.Count > MaxConditioning) person.Conditioning.Remove(person.Conditioning.Keys.First());
            }
            else person.Previous[_frame] = memory;
            Forget(person);

            results.Add((id, score[0] > 0, mask));
        }
        return Compose(results, gridWidth, gridHeight);
    }

    /// <summary>Bounds a person's memory: the pointers of the last 16 frames, the spatial memories of the last 6 (≈ 2 MB each).</summary>
    private void Forget(Person person)
    {
        foreach (var (frame, memory) in person.Previous.ToList())
        {
            if (frame < _frame - Sam2Sessions.MaxPointers) person.Previous.Remove(frame);
            else if (frame < _frame - (Sam2Sessions.NumMaskMem - 1) && memory.Features is not null)
                person.Previous[frame] = memory with { Features = null, Position = null };
        }
    }

    /// <summary>The current frame conditioned on a person's memory (SAM2Base._prepare_memory_conditioned_features).</summary>
    private float[] Attend(Person person, float[] vf, float[] vp, CancellationToken cancellationToken)
    {
        // Spatial memories: the prompted frames (t_pos 0), then frame − (7 − t_pos) for t_pos 1..6.
        var spatial = new List<(FrameMemory Memory, int TposIndex)>();
        foreach (var memory in person.Conditioning.Values) spatial.Add((memory, Sam2Sessions.NumMaskMem - 1));
        for (var tPos = 1; tPos < Sam2Sessions.NumMaskMem; tPos++)
            if (person.Previous.TryGetValue(_frame - (Sam2Sessions.NumMaskMem - tPos), out var memory) && memory.Features is not null)
                spatial.Add((memory, Sam2Sessions.NumMaskMem - tPos - 1));

        var ns = spatial.Count * 4096;
        var mem = new float[ns * 64];
        var memPos = new float[ns * 64];
        for (var m = 0; m < spatial.Count; m++)
        {
            var (memory, tpos) = spatial[m];
            Array.Copy(memory.Features!, 0, mem, m * 4096 * 64, 4096 * 64);
            var position = memory.Position!;
            var offset = m * 4096 * 64;
            for (var i = 0; i < 4096; i++)
            for (var c = 0; c < 64; c++)
                memPos[offset + i * 64 + c] = position[i * 64 + c] + _s.MaskMemTpos[tpos * 64 + c];
        }

        // Object pointers: the prompted frames in the past (signed distance), then up to 15 previous frames.
        var pointers = new List<(int Distance, float[] Pointer)>();
        foreach (var (t, memory) in person.Conditioning)
            if (t <= _frame) pointers.Add((_frame - t, memory.Pointer));
        for (var d = 1; d < Sam2Sessions.MaxPointers; d++)
        {
            var t = _frame - d;
            if (t < 0) break;
            if (person.Previous.TryGetValue(t, out var memory)) pointers.Add((d, memory.Pointer));
        }
        var tNorm = pointers.Select(p => p.Distance / (float)(Sam2Sessions.MaxPointers - 1)).ToArray();
        float[] objPos;
        using (var pp = OnnxSessions.Run(_s.PointerPos, new[] { NamedOnnxValue.CreateFromTensor("t_norm", new DenseTensor<float>(tNorm, new[] { tNorm.Length })) }, cancellationToken))
            objPos = Out(pp, "obj_pos");
        var np = pointers.Count * 4;
        var ptr = new float[np * 64];
        var ptrPos = new float[np * 64];
        for (var p = 0; p < pointers.Count; p++)
        {
            Array.Copy(pointers[p].Pointer, 0, ptr, p * 256, 256);   // [256] → 4 tokens of 64, in order
            for (var k = 0; k < 4; k++) Array.Copy(objPos, p * 64, ptrPos, (p * 4 + k) * 64, 64);
        }

        // The current frame, sequence first: [4096, 1, 256] (its positional encoding is the same on every frame).
        var curr = new float[4096 * 256];
        var currPos = _currPos;
        var newPos = currPos is null;
        if (newPos) currPos = new float[4096 * 256];
        for (var c = 0; c < 256; c++)
        for (var i = 0; i < 4096; i++)
        {
            curr[i * 256 + c] = vf[c * 4096 + i];
            if (newPos) currPos![i * 256 + c] = vp[c * 4096 + i];
        }
        _currPos = currPos;
        using var att = OnnxSessions.Run(_s.MemoryAttention, new[]
        {
            NamedOnnxValue.CreateFromTensor("curr", new DenseTensor<float>(curr, new[] { 4096, 1, 256 })),
            NamedOnnxValue.CreateFromTensor("curr_pos", new DenseTensor<float>(currPos, new[] { 4096, 1, 256 })),
            NamedOnnxValue.CreateFromTensor("memory_spatial", new DenseTensor<float>(mem, new[] { ns, 1, 64 })),
            NamedOnnxValue.CreateFromTensor("memory_spatial_pos", new DenseTensor<float>(memPos, new[] { ns, 1, 64 })),
            NamedOnnxValue.CreateFromTensor("memory_ptr", new DenseTensor<float>(ptr, new[] { np, 1, 64 })),
            NamedOnnxValue.CreateFromTensor("memory_ptr_pos", new DenseTensor<float>(ptrPos, new[] { np, 1, 64 }))
        }, cancellationToken);
        return Out(att, "pix_feat_with_mem");
    }

    /// <summary>
    /// The people's coverage on the grid: each person's 256 × 256 logits upsampled bilinearly (align corners false) to the
    /// grid; at every cell only the person with the highest logit keeps it (the one in front — masks never overlap, never
    /// merge); its coverage is the sigmoid of that logit. Logits down to −6 (a coverage of ≈ 0.25 %) are kept for the soft
    /// edge; below that a cell belongs to nobody (and a mask's extent is taken from the half-covered cells).
    /// </summary>
    internal static IReadOnlyList<SegmentedPerson> Compose(IReadOnlyList<(int Id, bool Visible, float[] Logits)> people, int width, int height)
    {
        var xs = PersonDetectorMath.Taps(PeopleModel.SamMask, width);
        var ys = PersonDetectorMath.Taps(PeopleModel.SamMask, height);
        var coverage = people.Select(_ => new byte[width * height]).ToArray();
        const int n = PeopleModel.SamMask;
        for (var y = 0; y < height; y++)
        {
            var (y0, y1, fy) = ys[y];
            for (var x = 0; x < width; x++)
            {
                var (x0, x1, fx) = xs[x];
                var best = -1;
                var bestLogit = 0.0;
                for (var p = 0; p < people.Count; p++)
                {
                    if (!people[p].Visible) continue;
                    var l = people[p].Logits;
                    var top = l[y0 * n + x0] * (1 - fx) + l[y0 * n + x1] * fx;
                    var bottom = l[y1 * n + x0] * (1 - fx) + l[y1 * n + x1] * fx;
                    var v = top * (1 - fy) + bottom * fy;
                    if (v > -6 && (best < 0 || v > bestLogit)) (best, bestLogit) = (p, v);
                }
                if (best >= 0) coverage[best][y * width + x] = (byte)Math.Round(255 / (1 + Math.Exp(-bestLogit)));
            }
        }
        return people.Select((p, i) => new SegmentedPerson(p.Id, p.Visible, coverage[i])).ToList();
    }

    private static float[] Out(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string name) =>
        ((DenseTensor<float>)results.First(r => r.Name == name).AsTensor<float>()).Buffer.ToArray();

    public void Dispose()
    {
        _people.Clear();
        if (_ownsSessions) _s.Dispose();
    }
}
