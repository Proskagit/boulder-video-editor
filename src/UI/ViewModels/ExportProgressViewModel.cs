using System.Diagnostics;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// The export progress window (Phase 8 Step 7). It shows the service's <see cref="ExportProgress"/> as reported —
/// stage, <c>Done</c> of <c>Total</c> in the stage's own unit (audio samples of <c>AudioSampleCount</c>, frames of
/// <c>FrameCount</c>) and the percentage <c>Done / Total</c> — and offers Cancel.
/// <para>
/// The service reports from its worker thread into <see cref="Report"/>, which only stores the latest value; the
/// window pulls it on the UI thread with <see cref="Refresh"/> (a timer in the view, like the Preview's tick), so
/// no report is marshalled and none can reach the window after it closed.
/// </para>
/// <para>
/// D059 §4: also the encoder actually used (and why libx264 encodes when NVENC was chosen), the elapsed time and, in the
/// video stage, the current speed in frames per second (over the last few seconds) and the time left at that speed.
/// </para>
/// </summary>
public sealed partial class ExportProgressViewModel : ObservableObject, IProgress<ExportProgress>
{
    private readonly object _gate = new();
    private readonly Action _cancel;
    private ExportProgress? _latest;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Queue<(double Seconds, long Done)> _samples = new();
    private readonly Func<double>? _clock;

    /// <param name="clock">Seconds since the start (tests); null = a stopwatch started here.</param>
    public ExportProgressViewModel(string outputPath, Action cancel, Func<double>? clock = null)
    {
        OutputPath = outputPath;
        _cancel = cancel;
        _clock = clock;
    }

    /// <summary>"Кодировщик: libx264 medium CRF 18" once the video stage started; null before.</summary>
    [ObservableProperty] private string? _encoderText;

    /// <summary>Why libx264 encodes although NVENC was chosen (the service's reason); null otherwise.</summary>
    [ObservableProperty] private string? _fallback;

    /// <summary>"62 кадр/с · прошло 0:41 · осталось ≈ 1:20" in the video stage, "прошло 0:05" otherwise.</summary>
    [ObservableProperty] private string _speedText = "";

    public string OutputPath { get; }

    [ObservableProperty] private ExportStage? _stage;
    [ObservableProperty] private long _done;
    [ObservableProperty] private long _total;

    /// <summary>Done / Total in percent (0 while Total is 0).</summary>
    [ObservableProperty] private double _percent;

    /// <summary>"Audio", "Video", … — or "Cancelling…" once Cancel was pressed.</summary>
    [ObservableProperty] private string _stageText = Strings.Export_Starting;

    /// <summary>"1 234 / 5 678 frames (21 %)".</summary>
    [ObservableProperty] private string _detailText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;

    /// <summary>Called by the export service (any thread).</summary>
    public void Report(ExportProgress value)
    {
        lock (_gate) _latest = value;
    }

    /// <summary>Shows the latest report (UI thread).</summary>
    public void Refresh()
    {
        ExportProgress? latest;
        lock (_gate) latest = _latest;
        if (latest is not { } p) return;

        Stage = p.Stage;
        Done = p.Done;
        Total = p.Total;
        if (p.Encoder is { } encoder) EncoderText = string.Format(Strings.Export_EncoderLine, encoder);
        if (p.Fallback is { } fallback) Fallback = fallback;
        SpeedText = Speed(p);
        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        if (!IsCancelling) StageText = StageName(p.Stage);
        DetailText = p.Stage switch
        {
            ExportStage.Audio => string.Format(Strings.Export_AudioProgress, p.Done.ToString("N0"), p.Total.ToString("N0"), Percent.ToString("0")),
            ExportStage.Video => string.Format(Strings.Export_VideoProgress, p.Done.ToString("N0"), p.Total.ToString("N0"), Percent.ToString("0")),
            ExportStage.Finalizing => Strings.Export_Writing,
            _ => Strings.Export_PreparingEncoder
        };
    }

    private string Speed(ExportProgress p)
    {
        var now = _clock?.Invoke() ?? _elapsed.Elapsed.TotalSeconds;
        var elapsed = TimeFormat.ToShortString(Core.Common.MediaTime.FromSeconds(now));
        if (p.Stage != ExportStage.Video || p.Total <= 0) return string.Format(Strings.Export_ElapsedLine, elapsed);
        _samples.Enqueue((now, p.Done));
        while (_samples.Count > 2 && now - _samples.Peek().Seconds > 5) _samples.Dequeue();
        var (t0, d0) = _samples.Peek();
        if (now - t0 < 0.5 || p.Done <= d0) return string.Format(Strings.Export_ElapsedLine, elapsed);
        var fps = (p.Done - d0) / (now - t0);
        var left = TimeFormat.ToShortString(Core.Common.MediaTime.FromSeconds((p.Total - p.Done) / fps));
        return string.Format(Strings.Export_SpeedLine, fps.ToString("0.0"), elapsed, left);
    }

    public static string StageName(ExportStage stage) => stage switch
    {
        ExportStage.Preparing => Strings.Export_StagePreparing,
        ExportStage.Audio => Strings.Export_StageAudio,
        ExportStage.Video => Strings.Export_StageVideo,
        _ => Strings.Export_StageFinalizing
    };

    /// <summary>Requests cancellation (the export's token); the window stays until the export has finished.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (IsCancelling) return;
        IsCancelling = true;
        StageText = Strings.Export_Cancelling;
        _cancel();
    }

    private bool CanCancel() => !IsCancelling;
}
