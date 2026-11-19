using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D072 §3, the route from its first hold: in the markup mode, with «Предлагать трассу» on and the current route without holds
/// on the clip, the session's first plain click on the frame is the route's first hold → the window «Подождите, идёт анализ»
/// (<see cref="IsRouteAnalyzing"/>, «Отмена») while <see cref="RouteHoldFinder"/> searches the encoded markup frame
/// (<see cref="RouteHoldSearchOptions.Production"/>) → the route's holds are proposals on the frame: remove a proposal, add a
/// missed hold by a click (Enter adds it to the proposals, not yet to the clip), change the tolerance (no model call) →
/// «Принять» adds them all with one <see cref="Core.Interfaces.ITimelineEditService.AddHolds"/> — one Undo step, at the markup
/// frame's source time — and the markup goes on. «Отменить» drops the proposals. The analysis runs once per session; a failure
/// turns the click into an ordinary candidate.
/// </summary>
public sealed partial class HoldMarkupViewModel
{
    private enum RoutePhase { None, Analyzing, Reviewing }

    private RoutePhase _routePhase;
    private bool _routeTried;
    private CancellationTokenSource? _routeCts;
    private RouteHoldSearch? _routeSearch;
    private readonly HashSet<Guid> _routeRejected = new();
    private readonly List<RouteHoldCandidate> _routeManual = new();
    private readonly Dictionary<Guid, RouteHoldCandidate> _routeShown = new();

    /// <summary>D072 §3: «Предлагать трассу» — the first click on a hold proposes the whole route (a global setting, on by
    /// default).</summary>
    [ObservableProperty] private bool _isRouteProposalEnabled;

    partial void OnIsRouteProposalEnabledChanged(bool value) =>
        _settings?.Update(s => s with { RouteProposalsEnabled = value });

    /// <summary>A route is analysed or reviewed (from the first click to «Принять» / «Отменить»).</summary>
    [ObservableProperty] private bool _isRouteSelection;

    /// <summary>The window «Подождите, идёт анализ» is shown: the route's holds are being searched.</summary>
    [ObservableProperty] private bool _isRouteAnalyzing;

    /// <summary>The analysis's progress (0…1), shown in its window.</summary>
    [ObservableProperty] private double _routeProgress;

    /// <summary>The route's holds are proposed: the tolerance, «Принять» and «Отменить» are shown.</summary>
    [ObservableProperty] private bool _isRouteReview;

    /// <summary>D072 amendment 1: the colour the route's proposals are drawn in — the one its holds will get (the route's manual
    /// colour, or the Auto colour of the proposals as holds), so they read on the wall as the result will.</summary>
    [ObservableProperty] private string _proposalColorHex = RouteColor.Palette[0].Hex;

    /// <summary>Whether the proposals get the keyline (as the route's holds would).</summary>
    [ObservableProperty] private bool _proposalKeyline;

    /// <summary>How many holds are proposed now (the review's count; the list itself is gone — amendment 1).</summary>
    public int ProposalCount => _proposalSource.Count;


    /// <summary>Whether a plain click at <paramref name="point"/> starts the route's analysis (D072 §3).</summary>
    private bool StartsRoute(NormalizedPoint point) =>
        IsRouteProposalEnabled && !_routeTried && _routePhase == RoutePhase.None && _clip is not null &&
        (MarkupOf(_clip)?.Holds.IsEmpty ?? true) && HitHold(point) is null;

    /// <summary>The first hold's click: the window, the search on the markup frame, the review.</summary>
    private async Task AnalyzeRouteAsync(NormalizedPoint click)
    {
        if (_clip is not { } clip || _encoded is not { } encoded || MarkupFrame is not { } frame || _modeCts is not { } mode) return;
        _routeTried = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(mode.Token);
        _routeCts = cts;
        _routePhase = RoutePhase.Analyzing;
        IsRouteSelection = true;
        IsRouteAnalyzing = true;
        RouteProgress = 0;
        ModeText = Strings.Holds_RouteFindingHolds;
        RefreshCommands();
        try
        {
            var existing = ExistingContours(clip);
            var style = MarkupOf(clip) ?? new HoldMarkup();
            var tolerance = RouteHoldColor.DefaultTolerance;
            var sourceTime = _sourceTime;
            // Made here, on the UI thread: its reports come back here.
            var progress = new Progress<double>(p => { if (_routeCts == cts) RouteProgress = p; });
            var (found, color) = await Task.Run(async () =>
            {
                var result = await RouteHoldFinder.FindAsync(encoded, frame, click, existing, RouteHoldSearchOptions.Production, progress, cts.Token);
                if (result.Search is not { } s) return (result, default((string, bool)));
                // Amendment 1: the colour the holds would get — the proposals drawn in it.
                var holds = RouteHoldFinder.ToHolds(frame, s.Select(tolerance), existing, sourceTime);
                return (result, RouteColor.Resolve(style with { Holds = style.Holds.AddRange(holds) }));
            });
            if (_routeCts != cts) return;
            if (found.Search is not { } search)
            {
                EndRoute();
                _status.Report(found.Error!);
                await ManualClickAsync(click);
                return;
            }

            _routeSearch = search;
            (ProposalColorHex, ProposalKeyline) = color;
            _routePhase = RoutePhase.Reviewing;
            IsRouteAnalyzing = false;
            IsRouteReview = true;
            RouteProgress = 1;
            RefreshRoute();
            if (_proposalSource.Count == 0) _status.Report(Messages.RouteSelectionNothingNew);
            else _status.Report(string.Format(Strings.Holds_RouteFound, _proposalSource.Count));
            ModeText = Strings.Holds_RouteReview;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // «Отмена» or the mode left meanwhile: nothing changed
            if (_routeCts == cts) EndRoute();
        }
        catch (Exception ex) when (_routeCts == cts)
        {
            EndRoute();
            _status.ReportWarning(string.Format(Strings.Holds_NotPossible, ex.Message));
            await ManualClickAsync(click);
        }
        catch (Exception)
        {
            // the mode left meanwhile (its encoded frame disposed under the search): nothing to report, nothing changed
        }
        finally
        {
            if (_routeCts == cts) _routeCts = null;   // the analysis is over: nothing left to cancel
            cts.Dispose();
            RefreshCommands();
        }
    }

    /// <summary>A failed analysis: the click is an ordinary candidate at that point (never lost).</summary>
    private async Task ManualClickAsync(NormalizedPoint point)
    {
        if (!IsActive || IsPreparing || _encoded is null || MarkupFrame is null) return;
        _points.Clear();
        _points.Add(new PointD(point.X * MarkupFrame.Width, point.Y * MarkupFrame.Height));
        await PredictAsync();
    }

    private bool CanCancelRouteAnalysis() => IsRouteAnalyzing;

    /// <summary>«Отмена» in the analysis window (or Esc): nothing changed, the markup goes on by hand.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRouteAnalysis))]
    private void CancelRouteAnalysis()
    {
        if (!IsRouteAnalyzing) return;
        EndRoute();
        RefreshCommands();
    }

    /// <summary>The proposals now: the control hold, the user's added holds, then the selection at the tolerance —
    /// without the rejected ones. The review list and the overlay show them all.</summary>
    private void RefreshRoute()
    {
        if (_routeSearch is null) return;
        // Amendment 2: the tolerance is no longer the user's (its control re-selected among the holds already found and did
        // not help) — D048's 0.08 with the strict gates.
        var selected = _routeSearch.Select(RouteHoldColor.DefaultTolerance, _routeRejected);
        var shown = selected.Where(c => c.IsControl).Concat(_routeManual).Concat(selected.Where(c => !c.IsControl)).ToList();
        var selectedId = SelectedProposal?.Proposal.Id;
        _routeShown.Clear();
        _proposalSource.Clear();
        foreach (var c in shown)
        {
            _routeShown[c.Id] = c;
            _proposalSource.Add(new HoldProposal(c.Id, c.Contour, c.Centroid, c.Score, c.AreaFraction));
        }
        RefreshProposals();
        OnPropertyChanged(nameof(ProposalCount));
        var index = selectedId is { } id ? Proposals.ToList().FindIndex(p => p.Proposal.Id == id) : -1;
        if (index >= 0) SelectedProposalIndex = index;
    }

    /// <summary>Enter on a candidate during the review: the hold joins the route's proposals (not yet the clip).</summary>
    private async Task AddRouteCandidate(DecodedFrame frame, byte[] component, ImmutableArray<NormalizedPoint> contour)
    {
        if (_routeSearch is null || MaskCrop.From(component, frame.Width, frame.Height) is not { } crop) return;
        var generation = ++_generation;
        var (surface, _) = await Task.Run(() => HoldColorSampler.Sample(frame, component, Array.Empty<IReadOnlyList<NormalizedPoint>>()));
        if (generation != _generation || !IsRouteReview) return;
        _routeManual.Add(new RouteHoldCandidate(Guid.NewGuid(), contour, HoldProposalService.ComputeCentroid(contour), 1,
            (double)crop.Area / ((long)frame.Width * frame.Height), surface, RouteHoldColor.Distance(surface, _routeSearch.Reference),
            crop, IsControl: false));
        ClearCandidate();
        RefreshRoute();
    }

    /// <summary>A proposal removed during the review stays removed whatever the tolerance.</summary>
    private void RejectRouteProposal(Guid id)
    {
        _routeRejected.Add(id);
        _routeManual.RemoveAll(c => c.Id == id);
        RefreshRoute();
    }

    private bool CanAcceptRoute() => IsRouteReview && !IsRefiningProposals && _proposalSource.Count > 0 && _clip is not null && MarkupFrame is not null;

    /// <summary>«Принять»: every proposal of the review as holds of the markup frame — one step; the markup goes on.</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptRoute))]
    private async Task AcceptRoute() => await AcceptRouteCoreAsync();

    /// <summary>The accept itself; true when the holds were added.</summary>
    private async Task<bool> AcceptRouteCoreAsync()
    {
        if (!CanAcceptRoute()) return false;
        var clip = _clip!;
        var frame = MarkupFrame!;
        var accepted = _proposalSource.Select(p => _routeShown[p.Id]).ToList();
        var existing = ExistingContours(clip);
        var room = Math.Max(0, HoldMarkupRules.MaxHolds - existing.Count);
        if (accepted.Count > room)
        {
            _status.ReportWarning(string.Format(Messages.Holds_MaxHolds, HoldMarkupRules.MaxHolds));
            return false;
        }
        if (_lock.IsLocked)
        {
            _status.ReportWarning(Strings.Holds_OutlineExportRunning);
            return false;
        }

        IsRefiningProposals = true;
        RefreshCommands();
        try
        {
            var sourceTime = _sourceTime;
            var holds = await Task.Run(() => SmoothHolds(RouteHoldFinder.ToHolds(frame, accepted, existing, sourceTime), frame.Width, frame.Height));
            if (!IsRouteReview || !ReferenceEquals(_clip, clip)) return false;   // left meanwhile: nothing added
            var result = _edit.AddHolds(clip.Id, holds, RouteForEdit());
            if (!result.Success)
            {
                _status.ReportWarning(result.Message ?? Strings.Holds_NotAdded);
                return false;
            }
            if (!result.NoChange) _status.Report(string.Format(Strings.Holds_AcceptedAll, holds.Count));
            EndRoute();
            LoadStyle();
            UpdateOverlay();
            return true;
        }
        catch (Exception ex)
        {
            _status.ReportError(string.Format(Strings.Holds_AddFailed, ex.Message));
            return false;
        }
        finally
        {
            IsRefiningProposals = false;
            RefreshCommands();
        }
    }

    private bool CanCancelRoute() => IsRouteReview;

    /// <summary>«Отменить»: the proposals dropped, nothing changed; the markup goes on.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRoute))]
    private void CancelRoute()
    {
        if (!IsRouteReview) return;
        EndRoute();
        RefreshCommands();
    }

    /// <summary>The route's analysis or review ended (accepted, cancelled, failed): its state forgotten, the markup by hand.</summary>
    private void EndRoute()
    {
        var cts = _routeCts;
        _routeCts = null;
        cts?.Cancel();
        _routePhase = RoutePhase.None;
        _routeSearch = null;
        _routeRejected.Clear();
        _routeManual.Clear();
        _routeShown.Clear();
        IsRouteSelection = false;
        IsRouteAnalyzing = false;
        IsRouteReview = false;
        ClearProposals();
        OnPropertyChanged(nameof(ProposalCount));
        if (IsActive && !IsPreparing) ModeText = ReadyText;
    }

    /// <summary>Part of <see cref="Exit"/>: the route's state forgotten, the next session may analyse again.</summary>
    private void ResetRoute()
    {
        EndRoute();
        _routeTried = false;
    }

    private void RefreshRouteCommands()
    {
        CancelRouteAnalysisCommand.NotifyCanExecuteChanged();
        AcceptRouteCommand.NotifyCanExecuteChanged();
        CancelRouteCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<ImmutableArray<NormalizedPoint>> ExistingContours(VideoClip clip) =>
        (MarkupOf(clip)?.Holds ?? ImmutableArray<Hold>.Empty).Select(h => h.Points).ToList();
}
