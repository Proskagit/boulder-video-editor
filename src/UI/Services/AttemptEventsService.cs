using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.People;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// D078: the attempt's events from the video. Asks the people analysis for the windows of the FIT's route areas
/// (<see cref="FitAreaEventWindows"/>, in the background after everything the clips need, GPU only) while a setting of D078 §4 is
/// on; when an area's window is analysed, finds its events (<see cref="AttemptEvents"/>) and refines the areas the user has not
/// changed (<see cref="IFitMarkEditService.RefineFitAreasFromVideo"/>, one Undo step per batch, a status message). An area is
/// looked at once per session — an Undo of its refinement stays undone. Gives the lift-offs of a clip for the orange's magnet.
/// Runs on the UI context.
/// </summary>
public sealed class AttemptEventsService
{
    private readonly IProjectService _projects;
    private readonly PeopleAnalysisCoordinator _people;
    private readonly IFitMarkEditService _edit;
    private readonly IUserSettingsService _settings;
    private readonly StatusService _status;
    private readonly ILogger _logger;
    private readonly EditingLock? _lock;
    private readonly HashSet<Guid> _done = new();
    private (Guid Asset, PersonTracks Tracks, MediaTime From, MediaTime To, ImmutableArray<MediaTime> LiftOffs)? _liftCache;
    private readonly SynchronizationContext? _ui;
    private bool _applying;
    private bool _scheduled;
    private bool _gpuReported;

    /// <summary>The magnet looks for lift-offs this far around the dragged orange end (the rules read an attempt's window, not a
    /// whole training's clip: ≤ 3 climbers, ≤ 6 attempts each).</summary>
    public static readonly MediaTime MagnetWindow = MediaTime.FromSeconds(30);

    public AttemptEventsService(IProjectService projects, PeopleAnalysisCoordinator people, IFitMarkEditService edit,
        IUserSettingsService settings, StatusService status, EditingLock? editingLock = null, ILogger<AttemptEventsService>? logger = null)
    {
        _projects = projects;
        _people = people;
        _edit = edit;
        _settings = settings;
        _status = status;
        _lock = editingLock;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _ui = SynchronizationContext.Current;
        people.EventSpans = Spans;
        people.AnalysesChanged += (_, _) => RequestApply();
        people.StatusChanged += (_, _) => ReportGpu();
        // The windows may be analysed already (in memory from another project, by Follow, in the cache): looked at on every
        // change too, not only when an analysis grows.
        projects.ProjectChanged += (_, _) =>
        {
            _done.Clear();
            _liftCache = null;
            people.RefreshNeeds();
            RequestApply();
        };
        projects.TimelineChanged += (_, _) => RequestApply();
        settings.Changed += (_, _) =>
        {
            people.RefreshNeeds();
            RequestApply();
        };
        // An export locks editing: the refinement waits for its end (the export lock's rule).
        if (editingLock is not null)
            editingLock.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditingLock.IsLocked) && !editingLock.IsLocked) RequestApply();
            };
        people.RefreshNeeds();
        RequestApply();
    }

    /// <summary>The refinement runs later on the UI context, never inside the notification that asked for it: a timeline change
    /// is notified from within a command's Execute / Undo (and an edit gathered into one step) — the refinement is its own Undo
    /// step only after that command is recorded. Several requests meanwhile make one run. Assumes construction on the UI context
    /// (the app's DI does it there); without a context it would run synchronously and could land inside a notifying command.</summary>
    private void RequestApply()
    {
        if (_scheduled) return;
        _scheduled = true;
        void Run()
        {
            _scheduled = false;
            Apply();
        }
        if (_ui is not null) _ui.Post(_ => Run(), null);
        else Run();
    }

    private bool Fall => _settings.Current.RefineFallFromVideo;
    private bool LiftOff => _settings.Current.SnapRouteStartToLiftOff;

    /// <summary>The windows still to look at (none while both settings are off or the project has no usable training).</summary>
    private IReadOnlyList<FitAreaEventWindow> Pending(Project project) =>
        (Fall || LiftOff) && project.Training is { IsUsable: true }
            ? FitAreaEventWindows.Of(project).Where(w => !_done.Contains(w.TemplateId)).ToList()
            : Array.Empty<FitAreaEventWindow>();

    private IEnumerable<(Guid, MediaTime, MediaTime)> Spans(Project project) => Pending(project).Select(w => (w.AssetId, w.From, w.To));

    /// <summary>The areas whose windows are analysed: their events found, the areas refined in one step.</summary>
    private void Apply()
    {
        if (_applying || _lock?.IsLocked == true) return;
        var pending = Pending(_projects.Current);
        if (pending.Count == 0) return;
        var events = new Dictionary<Guid, AttemptEventSet>();
        // A video's areas once all its windows are analysed: one Undo step per video (D078 §3).
        foreach (var video in pending.GroupBy(w => w.AssetId))
        {
            if (!_people.Analyses.Tracks.TryGetValue(video.Key, out var tracks) ||
                !video.All(w => _people.HasTracks(w.AssetId, w.From, w.To)))
                continue;
            foreach (var window in video)
            {
                events[window.TemplateId] = AttemptEvents.Find(tracks, window.From, window.To);
                _done.Add(window.TemplateId);
            }
        }
        if (events.Count == 0) return;
        _applying = true;
        try
        {
            var result = _edit.RefineFitAreasFromVideo(events, Fall, LiftOff);
            if (result.Success && !result.NoChange && result.Message is { } message) _status.Report(message);
            _logger.LogInformation("Attempt events: {Areas} area(s) looked at, {Refined} refined.", events.Count, result.NoChange ? 0 : result.ClipIds.Count);
        }
        finally
        {
            _applying = false;
        }
        _people.RefreshNeeds();
    }

    /// <summary>D078 §2 Q2: without a GPU the events are not searched — said once per session.</summary>
    private void ReportGpu()
    {
        if (_gpuReported || !_people.EventsNeedGpu || Pending(_projects.Current).Count == 0) return;
        _gpuReported = true;
        _status.ReportWarning(Messages.People_EventsNeedGpu);
    }

    /// <summary>D078 §3: the lift-offs found around <paramref name="around"/> (a source time of <paramref name="clip"/>, ±
    /// <see cref="MagnetWindow"/>, within the clip) for the orange's magnet — empty while «Притягивать начало трассы к отрыву» is
    /// off or its video's tracks are not analysed there. The window is fixed when a drag starts (cached for its source time).</summary>
    public ImmutableArray<MediaTime> LiftOffsAround(VideoClip clip, MediaTime around)
    {
        if (!LiftOff || !_people.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var tracks)) return ImmutableArray<MediaTime>.Empty;
        var from = around - MagnetWindow < clip.SourceIn ? clip.SourceIn : around - MagnetWindow;
        var to = around + MagnetWindow > clip.SourceOut ? clip.SourceOut : around + MagnetWindow;
        if (_liftCache is { } c && c.Asset == clip.MediaAssetId && ReferenceEquals(c.Tracks, tracks) && c.From <= around && around <= c.To &&
            Math.Abs(around.Ticks - (c.From.Ticks + c.To.Ticks) / 2) < MagnetWindow.Ticks / 2)
            return c.LiftOffs;
        var lifts = AttemptEvents.Find(tracks, from, to).LiftOffs;
        _liftCache = (clip.MediaAssetId, tracks, from, to, lifts);
        return lifts;
    }
}
