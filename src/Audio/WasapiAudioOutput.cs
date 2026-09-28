using System.Runtime.InteropServices;
using AiVideoEditor.Core.Playback;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace AiVideoEditor.Audio;

/// <summary>Configuration of <see cref="WasapiAudioOutput"/>; to be tuned on real hardware.</summary>
public sealed class WasapiOutputSettings
{
    /// <summary>Requested WASAPI buffer latency (shared mode).</summary>
    public int LatencyMilliseconds { get; init; } = 100;
}

/// <summary>
/// <see cref="IAudioOutput"/> on WASAPI shared mode (NAudio 2.2.1), default render device,
/// 48 kHz stereo float. Behaviour established by the WASAPI spike (see progress.md):
/// <list type="bullet">
/// <item><c>WasapiOut.GetPosition()</c> reports bytes actually played (IAudioClock), in units of
/// <c>OutputWaveFormat</c>; it returns 0 when stopped and restarts from 0 — so the clock adds
/// every finished session to a base read right before <c>Stop()</c>, and never runs backwards;</item>
/// <item><c>Pause()</c> only stops feeding (queued audio keeps playing) — never used; Stop flushes;</item>
/// <item>COM objects are apartment-bound: the device is created and controlled on the thread that
/// calls <see cref="TryStart"/>/<see cref="Stop"/> (the UI thread); the position may be read from others;</item>
/// <item>an <c>OutputWaveFormat</c> other than 48 kHz stereo float is refused (the service then
/// falls back to the Stopwatch).</item>
/// </list>
/// Which device (D024 Step 9.3, product owner option B): every start — Play, and the restart after a seek —
/// asks for the current default render device and compares its endpoint id with the id of the device the output
/// is open on; a different id (the default was changed, or the old device was removed) closes the old output and
/// opens one on the new default, the same id keeps the open output. Nothing switches devices during playback: a
/// device that disappears while playing stops the output with an error (<see cref="HasFailed"/>, D013 — the
/// service continues on the Stopwatch) and the next start opens the default device of that moment.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly ILogger<WasapiAudioOutput> _logger;
    private readonly WasapiOutputSettings _settings;
    private readonly IAudioEndpoints _endpoints;
    private readonly PlayedFramesClock _clock;
    private readonly SourceProvider _provider = new();

    private IWavePlayer? _output;
    private string? _deviceId;      // endpoint id of the device _output is open on
    private int _blockAlign;
    private long _baseFrames;       // frames played by finished sessions
    private long _lastSessionFrames;
    private bool _playing;
    private volatile bool _failed;

    public WasapiAudioOutput(ILogger<WasapiAudioOutput> logger, WasapiOutputSettings? settings = null)
        : this(logger, new WasapiEndpoints(), settings)
    {
    }

    /// <summary>Test seam: <paramref name="endpoints"/> replaces the system's devices.</summary>
    internal WasapiAudioOutput(ILogger<WasapiAudioOutput> logger, IAudioEndpoints endpoints, WasapiOutputSettings? settings = null)
    {
        _logger = logger;
        _endpoints = endpoints;
        _settings = settings ?? new WasapiOutputSettings();
        _clock = new PlayedFramesClock(this);
    }

    public IReferenceClock Clock => _clock;

    public bool HasFailed => _failed;

    public bool TryStart(IAudioSampleSource source)
    {
        if (_playing) return true;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var defaultId = _endpoints.DefaultRenderDeviceId()
                            ?? throw new InvalidOperationException("There is no default audio output device.");
            if (_output is null || _failed || !string.Equals(_deviceId, defaultId, StringComparison.OrdinalIgnoreCase))
                CreateDevice(defaultId);
            _provider.Source = source;
            _failed = false;
            _lastSessionFrames = 0;
            _output!.Play();
            _playing = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio output could not be started.");
            _provider.Source = null;
            DisposeDevice();
            return false;
        }
    }

    public void Stop()
    {
        if (!_playing) return;
        _baseFrames += SessionFrames(); // GetPosition() is 0 once stopped: read it first
        _lastSessionFrames = 0;
        _playing = false;
        try
        {
            _output?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the audio output failed.");
        }
        _provider.Source = null;
    }

    private void CreateDevice(string deviceId)
    {
        var replaced = _deviceId;
        DisposeDevice(); // the old output, if any, is closed before the new one opens
        var output = _endpoints.Open(deviceId, _settings.LatencyMilliseconds);
        try
        {
            output.Init(_provider);
            var format = output.OutputWaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.SampleRate != AudioFormat.SampleRate || format.Channels != AudioFormat.Channels)
                throw new NotSupportedException($"Unexpected WASAPI output format {format}.");
            _blockAlign = format.BlockAlign;
            output.PlaybackStopped += OnPlaybackStopped;
            _output = output;
            _deviceId = deviceId;
            if (replaced is not null && !string.Equals(replaced, deviceId, StringComparison.OrdinalIgnoreCase))
                _logger.LogInformation("Audio output moved to the current default device {Device} (was {Previous}), {Latency} ms latency.",
                    deviceId, replaced, _settings.LatencyMilliseconds);
            else
                _logger.LogInformation("Audio output: device {Device}, {Latency} ms latency.", deviceId, _settings.LatencyMilliseconds);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return; // our own Stop()
        _logger.LogWarning(e.Exception, "Audio output stopped unexpectedly.");
        _failed = true;
    }

    /// <summary>Frames played in the current session; the last known value if the device fails.</summary>
    private long SessionFrames()
    {
        if (!_playing || _output is not IWavePosition position) return 0;
        try
        {
            _lastSessionFrames = Math.Max(_lastSessionFrames, position.GetPosition() / _blockAlign);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NullReferenceException)
        {
            _failed = true;
        }
        return _lastSessionFrames;
    }

    private void DisposeDevice()
    {
        if (_output is null) return;
        _output.PlaybackStopped -= OnPlaybackStopped;
        try
        {
            _output.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the audio output failed.");
        }
        _output = null;
        _deviceId = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeDevice();
    }

    private sealed class PlayedFramesClock : IReferenceClock
    {
        private readonly WasapiAudioOutput _owner;
        public PlayedFramesClock(WasapiAudioOutput owner) => _owner = owner;
        public ReferenceTime Now => new(_owner._baseFrames + _owner.SessionFrames(), AudioFormat.SampleRate);
    }

    /// <summary>Hands WASAPI's buffer to the mixer as floats: no copy, no allocation.</summary>
    private sealed class SourceProvider : IWaveProvider
    {
        private volatile IAudioSampleSource? _source;

        public IAudioSampleSource? Source { get => _source; set => _source = value; }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            if (_source is { } source) source.Read(samples);
            else samples.Clear();
            return count;
        }
    }
}
