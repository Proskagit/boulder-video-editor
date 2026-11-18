using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiVideoEditor.Vision.Audio;

/// <summary>
/// The pinned audio models "audio-v1" (D064 §4–§5, docs/AI_MODEL.md "Audio models"), local, never downloaded by the application:
/// <list type="bullet">
/// <item>DeepFilterNet3 (Hendrik Schröter et al., MIT OR Apache-2.0) as the streaming single-graph ONNX of the torchDF export
/// (github.com/grazder/DeepFilterNet), from huggingface.co/kimtos-labs/denoiser-dfn3 at ffc593d: 48 kHz mono, a hop of 480
/// samples in and out, a state of 45 304 floats, the output 1 440 samples behind the input (measured).</item>
/// <item>Silero VAD v5 (Silero Team, MIT), github.com/snakers4/silero-vad <c>src/silero_vad/data/silero_vad.onnx</c>: 16 kHz,
/// 512 samples per window with 64 of context, an LSTM state [2, 1, 128].</item>
/// </list>
/// Both run on the CPU (DeepFilterNet3's graph fails to initialise under DirectML; a 480-sample hop gains nothing on a GPU).
/// A file with another size or SHA-256 is never loaded.
/// </summary>
public static class AudioModel
{
    public const string Id = "audio-v1";

    /// <summary>The folder name under %LOCALAPPDATA%\BoulderVideoEditor\models.</summary>
    public const string FolderName = "audio-v1";

    public static readonly HoldModelFile Voice =
        new("denoiser_model.onnx", 16_104_687, "fe5eb64fa2e4154c83f8e4935e82871c850c154387ee892e0ab65fe179e7d8c9");

    public static readonly HoldModelFile Speech =
        new("silero_vad.onnx", 2_327_524, "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3");

    public const int VoiceHop = 480;
    public const int VoiceStateLength = 45_304;
    public const int VoiceDelay = 1_440;

    public const int SpeechWindow = 512;
    public const int SpeechContext = 64;
}

/// <summary>A pinned model checked once (its size and SHA-256), then loaded once on the CPU; the failure's message kept.</summary>
public abstract class PinnedCpuModel : IDisposable
{
    private readonly HoldModelVerifier _verifier;
    private readonly HoldModelFile _file;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private string? _failure;

    protected PinnedCpuModel(string folder, HoldModelFile file, ILogger? logger)
    {
        _file = file;
        _verifier = new HoldModelVerifier(folder, new[] { file }, AudioModel.Id);
        _logger = logger ?? NullLogger.Instance;
    }

    public string Folder => _verifier.Folder;

    protected InferenceSession Session => _session ?? throw new InvalidOperationException("The model is not loaded (CheckAsync first).");

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (_session is not null) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null) return null;
            if (_failure is not null) return _failure;
            var status = await _verifier.VerifyAsync(null, ct).ConfigureAwait(false);
            if (!status.IsReady) return status.Message;   // not kept: the user may install it meanwhile
            try
            {
                var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _session = new InferenceSession(_verifier.PathOf(_file), options);
                return null;
            }
            catch (OnnxRuntimeException ex)
            {
                _logger.LogError(ex, "The audio model {File} failed to load.", _file.Name);
                return _failure = string.Format(Core.Localization.Messages.Audio_ModelFailed, _file.Name, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>D064 §5: DeepFilterNet3, one stream per processing chunk (its own state); the session is shared by the threads.</summary>
public sealed class DeepFilterNetEnhancer : PinnedCpuModel, IVoiceEnhancer
{
    public DeepFilterNetEnhancer(string folder, ILogger<DeepFilterNetEnhancer>? logger = null) : base(folder, AudioModel.Voice, logger)
    {
    }

    public int HopSamples => AudioModel.VoiceHop;

    public int DelaySamples => AudioModel.VoiceDelay;

    public IVoiceEnhancerStream Open() => new Stream(Session);

    private sealed class Stream(InferenceSession session) : IVoiceEnhancerStream
    {
        private readonly float[] _frame = new float[AudioModel.VoiceHop];
        private float[] _states = new float[AudioModel.VoiceStateLength];
        // The model's own attenuation limit mixes the noisy input in without its delay (a comb filter): never used — 0 dB is
        // "no limit"; the strength is the processor's delayed dry / wet blend (D064 §5).
        private readonly DenseTensor<float> _limit = new(new[] { 0f }, Array.Empty<int>());

        public void Process(ReadOnlySpan<float> input, Span<float> output)
        {
            input.CopyTo(_frame);
            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_frame", new DenseTensor<float>(_frame, new[] { AudioModel.VoiceHop })),
                NamedOnnxValue.CreateFromTensor("states", new DenseTensor<float>(_states, new[] { AudioModel.VoiceStateLength })),
                NamedOnnxValue.CreateFromTensor("atten_lim_db", _limit)
            });
            foreach (var result in results)
            {
                if (result.Name == "enhanced_audio_frame")
                {
                    var tensor = result.AsTensor<float>();
                    for (var i = 0; i < output.Length; i++) output[i] = tensor.GetValue(i);
                }
                else if (result.Name == "new_states")
                {
                    _states = result.AsTensor<float>().ToArray();
                }
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>D064 §4: Silero VAD v5 at 16 kHz, one stream per analysed sound.</summary>
public sealed class SileroSpeechModel : PinnedCpuModel, ISpeechModel
{
    public SileroSpeechModel(string folder, ILogger<SileroSpeechModel>? logger = null) : base(folder, AudioModel.Speech, logger)
    {
    }

    public int WindowSamples => AudioModel.SpeechWindow;

    public ISpeechModelStream Open() => new Stream(Session);

    private sealed class Stream(InferenceSession session) : ISpeechModelStream
    {
        private readonly float[] _input = new float[AudioModel.SpeechContext + AudioModel.SpeechWindow];
        private float[] _state = new float[2 * 128];
        private readonly DenseTensor<long> _rate = new(new[] { 16_000L }, Array.Empty<int>());

        public float Process(ReadOnlySpan<float> window16k)
        {
            window16k.CopyTo(_input.AsSpan(AudioModel.SpeechContext));
            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, new[] { 1, _input.Length })),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
                NamedOnnxValue.CreateFromTensor("sr", _rate)
            });
            var probability = 0f;
            foreach (var result in results)
            {
                if (result.Name == "output") probability = result.AsTensor<float>().GetValue(0);
                else if (result.Name == "stateN") _state = result.AsTensor<float>().ToArray();
            }
            // The next window's context: the last 64 samples of this one.
            _input.AsSpan(_input.Length - AudioModel.SpeechContext).CopyTo(_input);
            return Math.Clamp(probability, 0f, 1f);
        }

        public void Dispose()
        {
        }
    }
}
