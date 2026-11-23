using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// D078 amendment 1 (the owner, 2026-11-23): the toolbar's right end beside the save state — the computer's processor and graphics
/// card load, always (every <see cref="LoadInterval"/>), and a badge while a background analysis runs (the people for Follow /
/// Occlusion, the attempt's events, the hold tracking, the sound): the first running one with its share done, «+N» for the
/// others, every one in the tooltip. Runs on the UI context.
/// </summary>
public sealed partial class BackgroundActivityViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan LoadInterval = TimeSpan.FromSeconds(1.5);

    private readonly PeopleAnalysisCoordinator? _people;
    private readonly CameraMotionCoordinator? _cameraMotion;
    private readonly AudioProcessingCoordinator? _audio;
    private readonly ISystemLoadMonitor? _load;
    private readonly CancellationTokenSource _stop = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private string _activityTip = "";
    [ObservableProperty] private double _activityProgress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _loadText = "";

    public BackgroundActivityViewModel(PeopleAnalysisCoordinator? people = null, CameraMotionCoordinator? cameraMotion = null,
        AudioProcessingCoordinator? audio = null, ISystemLoadMonitor? load = null)
    {
        _people = people;
        _cameraMotion = cameraMotion;
        _audio = audio;
        _load = load;
        if (people is not null) people.StatusChanged += (_, _) => Refresh();
        if (cameraMotion is not null) cameraMotion.StatusChanged += (_, _) => Refresh();
        if (audio is not null) audio.StatusChanged += (_, _) => Refresh();
        Refresh();
        HasLoad = load is not null;
        if (load is not null) _ = PollLoadAsync(load, _stop.Token);
    }

    public bool HasLoad { get; }

    public string LoadTip => Strings.Activity_LoadTip;

    /// <summary>The badge from the running analyses.</summary>
    public void Refresh()
    {
        var running = new[] { _people?.Activity, _cameraMotion?.Activity, _audio?.Activity }.Where(a => a is not null).Select(a => a!.Value).ToList();
        IsBusy = running.Count > 0;
        if (!IsBusy)
        {
            ActivityText = ActivityTip = "";
            HasProgress = false;
            return;
        }
        var first = running[0];
        HasProgress = first.Progress is not null;
        ActivityProgress = (first.Progress ?? 0) * 100;
        // «+N»: the other running analyses and this one's waiting runs (amendment 3: one run's share on the badge).
        var others = running.Count - 1 + Math.Max(0, first.Items - 1);
        ActivityText = Line(first) + (others > 0 ? " · " + string.Format(CultureInfo.CurrentCulture, Strings.Activity_More, others) : "");
        ActivityTip = string.Join(Environment.NewLine, running.Select(a =>
            Line(a) + (a.Items > 1 ? " — " + string.Format(CultureInfo.CurrentCulture, Strings.Activity_Items, a.Items) : "")));
    }

    private static string Line(BackgroundActivity a) =>
        a.Progress is { } p ? string.Format(CultureInfo.CurrentCulture, "{0} {1:0} %", a.Label, Math.Clamp(p, 0, 1) * 100) : a.Label;

    /// <summary>The load as text: «ЦП  23 % · ГП  61 %», a dash where not measurable; each number padded to three digits with
    /// figure spaces (the width of a digit) so the text never shifts as the numbers change (amendment 2).</summary>
    public static string FormatLoad(SystemLoad load)
    {
        static string Percent(double? v) => (v is { } x ? Math.Round(Math.Clamp(x, 0, 100)).ToString("0", CultureInfo.CurrentCulture) : "—")
            .PadLeft(3, '\u2007') + " %";
        return string.Format(CultureInfo.CurrentCulture, Strings.Activity_Load, Percent(load.Cpu), Percent(load.Gpu));
    }

    private async Task PollLoadAsync(ISystemLoadMonitor load, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sample = await Task.Run(load.Sample, cancellationToken);
                LoadText = FormatLoad(sample);
                await Task.Delay(LoadInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _stop.Cancel();
}
