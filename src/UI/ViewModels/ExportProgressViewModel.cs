using AiVideoEditor.Core.Export;
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
/// </summary>
public sealed partial class ExportProgressViewModel : ObservableObject, IProgress<ExportProgress>
{
    private readonly object _gate = new();
    private readonly Action _cancel;
    private ExportProgress? _latest;

    public ExportProgressViewModel(string outputPath, Action cancel)
    {
        OutputPath = outputPath;
        _cancel = cancel;
    }

    public string OutputPath { get; }

    [ObservableProperty] private ExportStage? _stage;
    [ObservableProperty] private long _done;
    [ObservableProperty] private long _total;

    /// <summary>Done / Total in percent (0 while Total is 0).</summary>
    [ObservableProperty] private double _percent;

    /// <summary>"Audio", "Video", … — or "Cancelling…" once Cancel was pressed.</summary>
    [ObservableProperty] private string _stageText = "Starting…";

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
        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        if (!IsCancelling) StageText = StageName(p.Stage);
        DetailText = p.Stage switch
        {
            ExportStage.Audio => $"{p.Done:N0} / {p.Total:N0} audio samples ({Percent:0} %)",
            ExportStage.Video => $"{p.Done:N0} / {p.Total:N0} frames ({Percent:0} %)",
            ExportStage.Finalizing => "Writing the output file…",
            _ => "Preparing the encoder…"
        };
    }

    public static string StageName(ExportStage stage) => stage switch
    {
        ExportStage.Preparing => "Preparing",
        ExportStage.Audio => "Audio",
        ExportStage.Video => "Video",
        _ => "Finalizing"
    };

    /// <summary>Requests cancellation (the export's token); the window stays until the export has finished.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (IsCancelling) return;
        IsCancelling = true;
        StageText = "Cancelling…";
        _cancel();
    }

    private bool CanCancel() => !IsCancelling;
}
