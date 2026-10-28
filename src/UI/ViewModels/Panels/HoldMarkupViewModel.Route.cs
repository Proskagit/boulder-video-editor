using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D048 §2, the route selection at <c>T</c> ("Place holds") in the markup mode: paused on <c>T</c> the user presses the
/// button and clicks the control hold on the Preview → the click is carried to the clean frame 5 s earlier
/// (<see cref="RouteSelectionService"/>), the clean frame is encoded and the holds of the control hold's colour are found
/// (<see cref="RouteHoldFinder"/>) → the Preview shows the clean frame with the D047 review: remove a proposal, add a
/// missed hold by a click (Enter adds it to the route, not yet to the clip), change the tolerance (no model call) →
/// "Accept" adds them all with one <see cref="Core.Interfaces.ITimelineEditService.AddHolds"/> — one Undo step, the holds
/// marked at <c>t_C</c> — and the playhead goes back to <c>T</c>. Cancel, Esc or any failure: nothing changed, back to
/// <c>T</c>. The playhead never leaves <c>T</c> meanwhile (the clean frame may lie before the clip, in its handle), so the
/// overlay draws the clean frame itself (<see cref="ShowsMarkupFrame"/>).
/// </summary>
public sealed partial class HoldMarkupViewModel
{
    private enum RoutePhase { None, Picking, Preparing, Reviewing }

    private RouteSelectionService? _routeSelection;
    private RoutePhase _routePhase;
    private MediaTime _routeT;
    private RouteHoldSearch? _routeSearch;
    private FrameSize? _pickSize;
    private readonly HashSet<Guid> _routeRejected = new();
    private readonly List<RouteHoldCandidate> _routeManual = new();
    private readonly Dictionary<Guid, RouteHoldCandidate> _routeShown = new();

    /// <summary>Moves the timeline's playhead (set by the shell): the return to <c>T</c>.</summary>
    public Action<MediaTime> SeekTo { get; set; } = _ => { };

    /// <summary>A route selection is running (from the button to accept / cancel).</summary>
    [ObservableProperty] private bool _isRouteSelection;

    /// <summary>The route's holds are proposed on the clean frame: the tolerance, Accept and Cancel are shown.</summary>
    [ObservableProperty] private bool _isRouteReview;

    /// <summary>The overlay draws <see cref="MarkupFrame"/> over the clip's picture: the clean frame is not the frame under
    /// the playhead.</summary>
    [ObservableProperty] private bool _showsMarkupFrame;

    /// <summary>The colour tolerance (OKLab distance, L weighted ½); a change re-selects without the model.</summary>
    [ObservableProperty] private decimal? _routeTolerance = (decimal)RouteHoldColor.DefaultTolerance;

    public decimal MinRouteTolerance => (decimal)RouteHoldColor.MinTolerance;
    public decimal MaxRouteTolerance => (decimal)RouteHoldColor.MaxTolerance;

    private void InitRoute(RouteSelectionService? routeSelection) => _routeSelection = routeSelection;

    private bool CanPlaceHolds() => _routeSelection is not null && CanStart();

    /// <summary>"Place holds": the mode starts on the frame under the playhead (<c>T</c>) waiting for the control hold.</summary>
    [RelayCommand(CanExecute = nameof(CanPlaceHolds))]
    private void PlaceHolds()
    {
        if (!CanPlaceHolds()) return;
        var metadata = Asset(_clip!)!.Metadata!;
        var width = metadata.DisplayWidth ?? metadata.Width;
        var height = metadata.DisplayHeight ?? metadata.Height;
        if (width is not > 0 || height is not > 0)
        {
            _status.Report(Messages.RouteSelectionMediaUnavailable);
            return;
        }
        _modeCts = new CancellationTokenSource();
        _modePlayhead = Playhead();
        _routeT = _modePlayhead;
        _pickSize = new FrameSize(width.Value, height.Value);
        _routePhase = RoutePhase.Picking;
        IsRouteSelection = true;
        IsActive = true;
        IsPreparing = false;
        ModeText = Strings.Holds_RoutePickControl;
        UpdateOverlay();
        RefreshCommands();
    }

    /// <summary>The click on the control hold at <c>T</c>: the transfer, the clean frame, the search, the review.</summary>
    private async Task PickControlAsync(NormalizedPoint click)
    {
        if (_routePhase != RoutePhase.Picking || _clip is null || _routeSelection is null || _segmentation is null) return;
        if (Asset(_clip) is not { } asset) return;
        var clip = _clip;
        var cts = _modeCts ??= new CancellationTokenSource();
        var rate = _projects.Current.Settings.FrameRate;
        _routePhase = RoutePhase.Preparing;
        IsPreparing = true;
        PrepareProgress = 0;
        ModeText = Strings.Holds_RouteFindingCleanFrame;
        RefreshCommands();
        try
        {
            var status = await _segmentation.EnsureReadyAsync(null, cts.Token);
            if (_modeCts != cts) return;
            if (!status.IsReady)
            {
                FailRoute(status.Message ?? Strings.Holds_ModelUnavailable);
                return;
            }
            if (_segmentation.Device == InferenceDevice.Cpu) _status.Report(CpuMessage);

            var preparation = await _routeSelection.PrepareAsync(clip, asset, _routeT.ToFrameFloor(rate), rate, click, cts.Token);
            if (_modeCts != cts) return;
            if (preparation.Prepared is not { } prepared)
            {
                FailRoute(preparation.Error!);
                return;
            }

            PrepareProgress = 0.3;
            ModeText = Strings.Holds_PreparingFrame;
            var encoded = await _segmentation.PrepareFrameAsync(prepared.Clean.Frame, cts.Token);
            if (_modeCts != cts)
            {
                encoded.Dispose();
                return;
            }
            _encoded = encoded;
            _sourceTime = prepared.Clean.SourceTime;
            MarkupFrame = prepared.Clean.Frame;
            ShowsMarkupFrame = true;
            UpdateOverlay();

            PrepareProgress = 0.5;
            ModeText = Strings.Holds_RouteFindingHolds;
            var existing = ExistingContours(clip);
            // Made here, on the UI thread: its reports come back here (the overlay reacts to the mode's properties).
            var progress = new Progress<double>(p => { if (_modeCts == cts) PrepareProgress = 0.5 + 0.5 * p; });
            var found = await Task.Run(() => RouteHoldFinder.FindAsync(encoded, prepared.Clean.Frame, prepared.ControlPoint, existing,
                new RouteHoldSearchOptions(), progress, cts.Token));
            if (_modeCts != cts) return;
            if (found.Search is not { } search)
            {
                FailRoute(found.Error!);
                return;
            }

            _routeSearch = search;
            _routePhase = RoutePhase.Reviewing;
            IsRouteReview = true;
            IsPreparing = false;
            PrepareProgress = 1;
            RefreshRoute();
            if (prepared.Frames.CleanAtFileStart) _status.Report(Strings.Holds_RouteCleanAtFileStart);
            if (_proposalSource.Count == 0) _status.Report(Messages.RouteSelectionNothingNew);
            ModeText = Strings.Holds_RouteReview;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // left meanwhile: nothing changed
        }
        catch (Exception ex) when (_modeCts == cts)
        {
            FailRoute(string.Format(Strings.Holds_NotPossible, ex.Message));
        }
        finally
        {
            RefreshCommands();
        }
    }

    /// <summary>A failure: the message, the mode left, back to <c>T</c>; the project untouched.</summary>
    private void FailRoute(string message)
    {
        _status.Report(message);
        CancelRoute();
    }

    partial void OnRouteToleranceChanged(decimal? value)
    {
        if (IsRouteReview) RefreshRoute();
    }

    /// <summary>The proposals now: the control hold, the user's added holds, then the selection at the tolerance —
    /// without the rejected ones. The review list and the overlay show them all.</summary>
    private void RefreshRoute()
    {
        if (_routeSearch is null) return;
        var tolerance = (double)Math.Clamp(RouteTolerance ?? (decimal)RouteHoldColor.DefaultTolerance, MinRouteTolerance, MaxRouteTolerance);
        var selected = _routeSearch.Select(tolerance, _routeRejected);
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

    /// <summary>"Accept": every proposal of the review as holds of the clean frame — one step — then back to <c>T</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptRoute))]
    private async Task AcceptRoute()
    {
        if (!CanAcceptRoute()) return;
        var clip = _clip!;
        var frame = MarkupFrame!;
        var accepted = _proposalSource.Select(p => _routeShown[p.Id]).ToList();
        var existing = ExistingContours(clip);
        var room = Math.Max(0, HoldMarkupRules.MaxHolds - existing.Count);
        if (accepted.Count > room)
        {
            _status.Report(string.Format(Messages.Holds_MaxHolds, HoldMarkupRules.MaxHolds));
            return;
        }
        if (_lock.IsLocked)
        {
            _status.Report(Strings.Holds_OutlineExportRunning);
            return;
        }

        IsRefiningProposals = true;
        RefreshCommands();
        try
        {
            var sourceTime = _sourceTime;
            var holds = await Task.Run(() => RouteHoldFinder.ToHolds(frame, accepted, existing, sourceTime));
            if (!IsRouteReview || !ReferenceEquals(_clip, clip)) return;   // left meanwhile: nothing added
            var result = _edit.AddHolds(clip.Id, holds);
            if (!result.Success)
            {
                _status.Report(result.Message ?? Strings.Holds_NotAdded);
                return;
            }
            _status.Report(string.Format(Strings.Holds_AcceptedAll, holds.Count));
            var t = _routeT;
            Exit();
            SeekTo(t);
        }
        catch (Exception ex)
        {
            _status.Report(string.Format(Strings.Holds_AddFailed, ex.Message));
        }
        finally
        {
            IsRefiningProposals = false;
            RefreshCommands();
        }
    }

    private bool CanCancelRoute() => IsRouteSelection;

    /// <summary>"Cancel": nothing changed, back to <c>T</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRoute))]
    private void CancelRoute()
    {
        if (!IsRouteSelection) return;
        var t = _routeT;
        Exit();
        SeekTo(t);
    }

    /// <summary>Part of <see cref="Exit"/>: the route selection's state forgotten.</summary>
    private void ResetRoute()
    {
        _routePhase = RoutePhase.None;
        _routeSearch = null;
        _pickSize = null;
        _routeRejected.Clear();
        _routeManual.Clear();
        _routeShown.Clear();
        IsRouteSelection = false;
        IsRouteReview = false;
        ShowsMarkupFrame = false;
    }

    private void RefreshRouteCommands()
    {
        PlaceHoldsCommand.NotifyCanExecuteChanged();
        AcceptRouteCommand.NotifyCanExecuteChanged();
        CancelRouteCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<ImmutableArray<NormalizedPoint>> ExistingContours(VideoClip clip) =>
        (clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty).Select(h => h.Points).ToList();
}
