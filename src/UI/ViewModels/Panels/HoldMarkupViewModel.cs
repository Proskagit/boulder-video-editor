using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// The AI hold markup of the selected video clip (D034): the Inspector's "Holds" section (the route outline's style —
/// one undoable step per change through <see cref="ITimelineEditService.SetHoldStyle"/>) and the markup mode over the
/// Preview.
/// <para>
/// Markup mode (§1): on a paused frame of the clip (Timeline mode, the playhead inside the clip) the frame under the
/// playhead is decoded at full size and encoded by the model in the background (progress, cancellation). A click creates a
/// candidate — the model's best visible mask (too large masks hidden, <see cref="HoldCandidateRules"/>); the
/// wheel or Tab cycles through the visible alternatives of a single-point candidate; Shift+click adds a positive point to
/// the same candidate (one mask, no alternatives); Enter confirms it (one undo step: the outer contour and the colour
/// samples, <see cref="HoldColorSampler"/>); Esc discards it. A click on a confirmed contour without a candidate selects
/// it; Delete removes it. A selected hold's contour can be corrected by hand (D035, <see cref="ContourEditing"/>): drag a
/// handle, Alt+click on the outline adds a vertex, Delete over a handle or a double-click on it removes one — each gesture
/// one undo step through <see cref="ITimelineEditService.ReplaceHoldContour"/>, the same hold, its colour samples kept.
/// Seek, Play, the Source mode, another selection, an export or another project leave the mode;
/// a candidate is discarded, the confirmed markup stays. Nothing changes the project except the confirm, the removal, a
/// contour correction and the style edits.
/// </para>
/// </summary>
public sealed partial class HoldMarkupViewModel : ViewModelBase
{
    public static string CpuMessage => Strings.Holds_Cpu;

    public static string RefineRejectedMessage => Strings.Holds_RefineRejected;

    private readonly ITimelineEditService _edit;
    private readonly IProjectService _projects;
    private readonly IHoldSegmentationService? _segmentation;
    private readonly IHoldMarkupFrameSource? _frames;
    private readonly StatusService _status;
    private readonly EditingLock _lock;
    private readonly SynchronizationContext? _ui;

    private VideoClip? _clip;
    private bool _syncing;

    // Markup mode state.
    private CancellationTokenSource? _modeCts;
    private IHoldFrame? _encoded;
    private MediaTime _sourceTime;
    private MediaTime _modePlayhead;
    private readonly List<PointD> _points = new();
    private HoldMaskSet? _maskSet;
    private ImmutableArray<int> _visible = ImmutableArray<int>.Empty;
    private int _alternative;
    private byte[]? _componentMask;
    private int _generation;
    private Task _confirming = Task.CompletedTask;
    private bool _predicting;
    private bool _confirmPending;

    public HoldMarkupViewModel(ITimelineEditService edit, IProjectService projects, StatusService status,
        IHoldSegmentationService? segmentation = null, IHoldMarkupFrameSource? frames = null, EditingLock? editingLock = null)
    {
        _edit = edit;
        _projects = projects;
        _status = status;
        _segmentation = segmentation;
        _frames = frames;
        _lock = editingLock ?? new EditingLock();
        _ui = SynchronizationContext.Current;

        _projects.TimelineChanged += (_, _) => OnTimelineChanged();
        _projects.ProjectChanged += (_, _) => { Exit(); SetClip(null); };
        _lock.PropertyChanged += (_, _) => { if (_lock.IsLocked) Exit(); RefreshCommands(); };
        if (_segmentation is not null)
            _segmentation.DeviceChanged += (_, _) => OnUi(() => _status.Report(CpuMessage));
    }

    /// <summary>The playhead (the timeline's), set by the shell.</summary>
    public Func<MediaTime> Playhead { get; set; } = () => MediaTime.Zero;

    /// <summary>Whether the Preview shows the timeline paused (not playing, not the Source mode), set by the shell.</summary>
    public Func<bool> IsPausedOnTimeline { get; set; } = () => true;

    public ImmutableArray<PaletteColor> Palette => RouteColor.Palette;
    public IReadOnlyList<string> ColorModes { get; } = new[] { Strings.Holds_ModeAuto, Strings.Holds_ModeManual };
    public IReadOnlyList<string> KeylineModes { get; } = new[] { Strings.Holds_ModeAuto, Strings.Holds_KeylineOnItem, Strings.Holds_KeylineOffItem };

    // ---- the Inspector section ------------------------------------------------------------------------------------

    /// <summary>A video clip is selected: the section is shown.</summary>
    [ObservableProperty] private bool _hasClip;
    [ObservableProperty] private int _holdCount;
    [ObservableProperty] private decimal? _strokeWidth = (decimal)HoldMarkup.DefaultStrokeWidth1080;
    [ObservableProperty] private int _colorModeIndex;
    [ObservableProperty] private string _colorHex = RouteColor.Palette[0].Hex;
    [ObservableProperty] private PaletteColor? _paletteSelection;
    [ObservableProperty] private int _keylineIndex;
    [ObservableProperty] private bool _keylineActive;
    [ObservableProperty] private bool _isManualColor;

    public decimal MinStrokeWidth => (decimal)HoldMarkupRules.MinStrokeWidth1080;
    public decimal MaxStrokeWidth => (decimal)HoldMarkupRules.MaxStrokeWidth1080;

    /// <summary>Shows <paramref name="clip"/>'s markup (null or not a video clip: the section is hidden). Another clip
    /// leaves the markup mode.</summary>
    public void SetClip(Clip? clip)
    {
        var video = clip as VideoClip;
        if (!ReferenceEquals(video, _clip)) Exit();
        _clip = video;
        HasClip = video is not null;
        LoadStyle();
        RefreshCommands();
    }

    private void LoadStyle()
    {
        _syncing = true;
        try
        {
            var markup = _clip?.HoldMarkup ?? new HoldMarkup();
            HoldCount = markup.Holds.Length;
            StrokeWidth = (decimal)markup.StrokeWidth1080;
            ColorModeIndex = markup.ColorMode == HoldColorMode.Manual ? 1 : 0;
            IsManualColor = markup.ColorMode == HoldColorMode.Manual;
            ColorHex = markup.ColorHex;
            PaletteSelection = RouteColor.Palette.FirstOrDefault(p => string.Equals(p.Hex, markup.ColorHex, StringComparison.OrdinalIgnoreCase)) is { Hex: not null } found ? found : null;
            KeylineIndex = (int)markup.Keyline;
            KeylineActive = markup.KeylineActive;
            LoadAnimation(markup);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnStrokeWidthChanged(decimal? value)
    {
        if (!_syncing && value is { } width) EditStyle(s => s with { StrokeWidth1080 = (double)width });
    }

    partial void OnColorModeIndexChanged(int value)
    {
        if (_syncing || value < 0) return;
        EditStyle(s => s with { ColorMode = value == 1 ? HoldColorMode.Manual : HoldColorMode.Auto, ManualColorHex = value == 1 ? ColorHex : null });
    }

    partial void OnPaletteSelectionChanged(PaletteColor? value)
    {
        if (_syncing || value is not { Hex: { } hex }) return;
        EditStyle(s => s with { ColorMode = HoldColorMode.Manual, ManualColorHex = hex });
    }

    partial void OnColorHexChanged(string value)
    {
        // A typed colour applies once it is a complete #RRGGBB.
        if (_syncing || !HoldMarkupRules.IsColorHex(value)) return;
        EditStyle(s => s with { ColorMode = HoldColorMode.Manual, ManualColorHex = value });
    }

    partial void OnKeylineIndexChanged(int value)
    {
        if (!_syncing && value >= 0) EditStyle(s => s with { Keyline = (KeylineMode)value });
    }

    private void EditStyle(Func<HoldStyle, HoldStyle> change)
    {
        if (_clip is null) return;
        if (_lock.IsLocked)
        {
            _status.Report(Strings.Holds_OutlineExportRunning);
            LoadStyle();
            return;
        }
        var current = HoldStyle.Of(_clip.HoldMarkup ?? new HoldMarkup());
        var result = _edit.SetHoldStyle(_clip.Id, change(current));
        if (!result.Success) _status.Report(result.Message ?? Strings.Holds_OutlineNotChanged);
        LoadStyle();
    }

    // ---- the animation (D036) ---------------------------------------------------------------------------------------

    public IReadOnlyList<string> AnimationOrders { get; } = new[] { Strings.Holds_OrderBottomToTop, Strings.Holds_OrderMarking };

    [ObservableProperty] private bool _isAnimated;
    [ObservableProperty] private int _animationOrderIndex;

    /// <summary>The start as a source time in seconds (D036 §2: a moment of the video, not of the timeline).</summary>
    [ObservableProperty] private decimal? _animationStart;
    [ObservableProperty] private decimal? _animationInterval = Seconds(HoldAnimation.DefaultInterval);
    [ObservableProperty] private decimal? _appearFade = Seconds(HoldAnimation.DefaultAppearFade);
    [ObservableProperty] private decimal? _stayAfterLast = Seconds(HoldAnimation.DefaultStayAfterLast);
    [ObservableProperty] private bool _stayUntilClipEnd;
    [ObservableProperty] private decimal? _disappearFade = Seconds(HoldAnimation.DefaultDisappearFade);

    /// <summary>The animation's length in words; the warning when it runs past the clip's end (not an error).</summary>
    [ObservableProperty] private string? _animationSummary;
    [ObservableProperty] private string? _animationWarning;

    public decimal MaxAnimationStart => Seconds(HoldMarkupRules.MaxStartSourceTime);
    public decimal MinAnimationInterval => Seconds(HoldMarkupRules.MinInterval);
    public decimal MaxAnimationInterval => Seconds(HoldMarkupRules.MaxInterval);
    public decimal MaxAnimationFade => Seconds(HoldMarkupRules.MaxFade);
    public decimal MaxAnimationStay => Seconds(HoldMarkupRules.MaxStay);

    private static decimal Seconds(MediaTime time) => time.Ticks / (decimal)TimeSpan.TicksPerSecond;

    /// <summary>Seconds as ticks, rounded to the nearest tick (the inputs carry at most three decimals).</summary>
    private static MediaTime Ticks(decimal seconds) => new((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));

    private void LoadAnimation(HoldMarkup markup)
    {
        var animation = markup.Animation;
        IsAnimated = animation is not null;
        var shown = animation ?? new HoldAnimation { StartSourceTime = _clip?.SourceIn ?? MediaTime.Zero };
        AnimationOrderIndex = (int)shown.Order;
        AnimationStart = Seconds(shown.StartSourceTime);
        AnimationInterval = Seconds(shown.Interval);
        AppearFade = Seconds(shown.AppearFade);
        StayAfterLast = Seconds(shown.StayAfterLast);
        StayUntilClipEnd = shown.StayUntilClipEnd;
        DisappearFade = Seconds(shown.DisappearFade);
        (AnimationSummary, AnimationWarning) = Summary(markup);
        AnimatedContours = IsActive && animation is not null
            ? markup.Holds.Select(h => h.Points).ToImmutableArray()
            : ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty;
    }

    /// <summary>"Animate" switched on: the defaults (D036), starting at the source time under the playhead when it is on
    /// the clip, otherwise at the clip's first source frame. Off: the animation goes (one undo step either way).</summary>
    partial void OnIsAnimatedChanged(bool value)
    {
        if (_syncing || _clip is null) return;
        EditAnimation(_ => value ? new HoldAnimation { StartSourceTime = PlayheadSourceTime() ?? _clip.SourceIn } : null);
    }

    partial void OnAnimationOrderIndexChanged(int value)
    {
        if (!_syncing && value >= 0) EditAnimation(a => a with { Order = (HoldAnimationOrder)value });
    }

    partial void OnAnimationStartChanged(decimal? value)
    {
        if (!_syncing && value is { } v) EditAnimation(a => a with { StartSourceTime = Ticks(v) });
    }

    partial void OnAnimationIntervalChanged(decimal? value)
    {
        if (!_syncing && value is { } v) EditAnimation(a => a with { Interval = Ticks(v) });
    }

    partial void OnAppearFadeChanged(decimal? value)
    {
        if (!_syncing && value is { } v) EditAnimation(a => a with { AppearFade = Ticks(v) });
    }

    partial void OnStayAfterLastChanged(decimal? value)
    {
        if (!_syncing && value is { } v) EditAnimation(a => a with { StayAfterLast = Ticks(v) });
    }

    partial void OnStayUntilClipEndChanged(bool value)
    {
        if (!_syncing) EditAnimation(a => a with { StayUntilClipEnd = value });
    }

    partial void OnDisappearFadeChanged(decimal? value)
    {
        if (!_syncing && value is { } v) EditAnimation(a => a with { DisappearFade = Ticks(v) });
    }

    private bool CanSetStartToPlayhead() => _clip?.HoldMarkup?.Animation is not null && PlayheadSourceTime() is not null;

    /// <summary>"Set to playhead": the animation starts on the frame under the playhead (its source time, D036 §2).</summary>
    [RelayCommand(CanExecute = nameof(CanSetStartToPlayhead))]
    private void SetStartToPlayhead()
    {
        if (PlayheadSourceTime() is { } start) EditAnimation(a => a with { StartSourceTime = start });
    }

    /// <summary>The source time of the frame under the playhead, when it is on the selected clip.</summary>
    private MediaTime? PlayheadSourceTime()
    {
        if (_clip is null) return null;
        var playhead = Playhead();
        if (playhead < _clip.TimelineStart || playhead >= _clip.TimelineEnd) return null;
        var rate = _projects.Current.Settings.FrameRate;
        var time = HoldAnimationRule.SourceTimeAt(_clip.TimelineStart, _clip.SourceIn, _clip.Speed, playhead.ToFrameFloor(rate), rate);
        return time < MediaTime.Zero ? MediaTime.Zero : time;
    }

    /// <summary>Applies <paramref name="change"/> to the clip's animation (switched off: only switching on applies).</summary>
    private void EditAnimation(Func<HoldAnimation, HoldAnimation?> change)
    {
        if (_clip is null) return;
        if (_lock.IsLocked)
        {
            _status.Report(Strings.Holds_AnimationExportRunning);
            LoadStyle();
            return;
        }
        var current = _clip.HoldMarkup?.Animation;
        if (current is null && !IsAnimated)
        {
            LoadStyle();
            return;
        }
        var result = _edit.SetHoldAnimation(_clip.Id, change(current ?? new HoldAnimation()));
        if (!result.Success) _status.Report(result.Message ?? Strings.Holds_AnimationNotChanged);
        LoadStyle();
        RefreshCommands();
    }

    /// <summary>The animation's length from its start (D036 §1) and a warning when it reaches past the clip's end.</summary>
    private (string?, string?) Summary(HoldMarkup markup)
    {
        if (_clip is null || markup.Animation is not { } animation) return (null, null);
        if (markup.Holds.IsEmpty) return (Strings.Holds_NoneMarked, null);
        var rate = _projects.Current.Settings.FrameRate;
        var outline = HoldOutline.Of(markup, _clip.TimelineStart, _clip.SourceIn, _clip.Speed, rate)!;
        var schedule = outline.Schedule!;
        string At(long frame) => string.Format(Strings.Unit_SecondsValue, (MediaTime.FromFrame(frame, rate) - MediaTime.FromFrame(schedule.StartFrame, rate)).TotalSeconds
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        var one = markup.Holds.Length == 1;
        var summary = schedule.EndFrame is { } end
            ? (one ? string.Format(Strings.Holds_SummaryOneGone, At(schedule.AllVisibleFrame), At(end))
                   : string.Format(Strings.Holds_SummaryAllGone, markup.Holds.Length, At(schedule.AllVisibleFrame), At(end)))
            : (one ? string.Format(Strings.Holds_SummaryOneStays, At(schedule.AllVisibleFrame))
                   : string.Format(Strings.Holds_SummaryAllStays, markup.Holds.Length, At(schedule.AllVisibleFrame)));
        var clipEnd = _clip.TimelineEnd.ToFrameCeiling(rate);
        var last = schedule.EndFrame ?? schedule.AllVisibleFrame;
        var warning = schedule.StartFrame >= clipEnd ? Strings.Holds_StartsAfterEnd
            : last > clipEnd ? Strings.Holds_RunsPastEnd
            : null;
        return (summary, warning);
    }

    private void OnTimelineChanged()
    {
        if (_clip is not null && !ClipIsInProject(_clip))
        {
            SetClip(null);
            return;
        }
        LoadStyle();
        if (IsActive && _clip is not null && TrackIsLocked(_clip)) Exit();   // locked meanwhile: nothing could be added
        if (IsActive) UpdateOverlay();
        RefreshCommands();
    }

    private bool TrackIsLocked(Clip clip) =>
        _projects.Current.Timeline.VideoTracks.FirstOrDefault(t => t.Clips.Contains(clip)) is { IsLocked: true };

    private bool ClipIsInProject(Clip clip) =>
        _projects.Current.Timeline.VideoTracks.Any(t => t.Clips.Contains(clip));

    // ---- markup mode ----------------------------------------------------------------------------------------------

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPreparing;
    [ObservableProperty] private double _prepareProgress;
    [ObservableProperty] private string? _modeText;
    [ObservableProperty] private bool _isLoupeOn = true;

    /// <summary>The mode's keys in full (the status line's tooltip).</summary>
    public string ModeHelp => Strings.Holds_ModeHelp;

    public static string SelectedText => Strings.Holds_SelectedText;
    public static string ReadyText => Strings.Holds_ReadyText;

    /// <summary>The candidate's contour (normalized source coordinates); empty without a candidate.</summary>
    [ObservableProperty] private ImmutableArray<NormalizedPoint> _candidateContour = ImmutableArray<NormalizedPoint>.Empty;
    [ObservableProperty] private bool _hasCandidate;
    [ObservableProperty] private string? _alternativeText;

    [ObservableProperty] private Guid? _selectedHoldId;
    [ObservableProperty] private ImmutableArray<NormalizedPoint> _selectedContour = ImmutableArray<NormalizedPoint>.Empty;

    /// <summary>Every confirmed contour while the mode is on and the route is animated (D036 §4): the overlay draws them
    /// thin and dashed, so a hold hidden on this frame can still be selected and corrected. Never in the export.</summary>
    [ObservableProperty] private ImmutableArray<ImmutableArray<NormalizedPoint>> _animatedContours =
        ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty;

    /// <summary>The selected hold's vertices shown as handles (indices into <see cref="SelectedContour"/>; a coarser
    /// subset for a dense contour — D035); empty without a selection.</summary>
    [ObservableProperty] private ImmutableArray<int> _selectedHandles = ImmutableArray<int>.Empty;

    /// <summary>The contour being dragged crosses itself: the release will be refused.</summary>
    [ObservableProperty] private bool _isContourInvalid;

    /// <summary>The markup frame (full size; the loupe draws from it) and where the clip's picture lies on the canvas.</summary>
    [ObservableProperty] private DecodedFrame? _markupFrame;
    [ObservableProperty] private LayerGeometry? _geometry;
    [ObservableProperty] private FrameSize _canvas;

    /// <summary>Whether markup can start now: a video clip with media and size, Timeline mode paused, the playhead inside
    /// the clip, no export, the model service present.</summary>
    public bool CanStart()
    {
        if (IsActive || _clip is null || _lock.IsLocked || _segmentation is null || _frames is null) return false;
        if (!IsPausedOnTimeline() || TrackIsLocked(_clip)) return false;   // a locked track refuses every hold anyway
        var playhead = Playhead();
        return playhead >= _clip.TimelineStart && playhead < _clip.TimelineEnd && Asset(_clip) is { Metadata: not null, IsMissing: false };
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (!CanStart()) return;
        var clip = _clip!;
        var asset = Asset(clip)!;
        var cts = new CancellationTokenSource();
        _modeCts = cts;
        _modePlayhead = Playhead();
        IsActive = true;
        IsPreparing = true;
        PrepareProgress = 0;
        ModeText = Strings.Holds_CheckingModel;
        RefreshCommands();
        try
        {
            // The checksum check reports its progress; once it is done the sessions are created (≈ 3 s on the first use in a
            // session, D034 §7 note) — said, so the wait is never silent.
            var status = await _segmentation!.EnsureReadyAsync(new Progress<double>(p =>
            {
                if (_modeCts != cts) return;
                PrepareProgress = p * 0.5;
                if (p >= 1) ModeText = Strings.Holds_LoadingModel;
            }), cts.Token);
            if (_modeCts != cts) return;   // left (and maybe restarted) meanwhile
            if (!status.IsReady)
            {
                _status.Report(status.Message ?? Strings.Holds_ModelUnavailable);
                Exit();
                return;
            }
            if (_segmentation.Device == InferenceDevice.Cpu) _status.Report(CpuMessage);

            ModeText = Strings.Holds_DecodingFrame;
            PrepareProgress = 0.55;
            var frame = await _frames!.GetAsync(clip, asset, _modePlayhead, _projects.Current.Settings.FrameRate, cts.Token);
            if (_modeCts != cts) return;
            ModeText = Strings.Holds_PreparingFrame;
            PrepareProgress = 0.7;
            var encoded = await _segmentation.PrepareFrameAsync(frame.Frame, cts.Token);
            if (cts.IsCancellationRequested || !IsActive || _modeCts != cts)
            {
                encoded.Dispose();
                return;
            }

            _encoded = encoded;
            _sourceTime = frame.SourceTime;
            MarkupFrame = frame.Frame;
            PrepareProgress = 1;
            IsPreparing = false;
            LoadStyle();   // the dashed contours of an animated route (D036)
            UpdateOverlay();
            ModeText = ReadyText;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // cancelled by the user or by leaving the mode: nothing changed
        }
        catch (Exception ex) when (_modeCts == cts)
        {
            _status.Report(string.Format(Strings.Holds_NotPossible, ex.Message));
            Exit();
        }
        catch (Exception)
        {
            // a session left meanwhile: nothing to report
        }
        finally
        {
            RefreshCommands();
        }
    }

    /// <summary>"Done": leaves the mode; the confirmed markup stays.</summary>
    [RelayCommand]
    private void Done() => Exit();

    /// <summary>Esc: discards the candidate; without one deselects; otherwise leaves the mode.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (!IsActive) return;
        if (IsPreparing) { Exit(); return; }
        if (!_confirming.IsCompleted) return;   // Enter came first: the hold is being added
        if (IsDraggingVertex) { CancelVertexDrag(); return; }
        if (HasCandidate) { ClearCandidate(); return; }
        if (SelectedHoldId is not null) { Select(null); return; }
        Exit();
    }

    [RelayCommand]
    private void NextAlternative() => CycleAlternative(1);

    [RelayCommand]
    private void PreviousAlternative() => CycleAlternative(-1);

    /// <summary>The wheel over the Preview: one step per notch.</summary>
    public void OnWheel(double delta)
    {
        if (delta != 0) CycleAlternative(delta < 0 ? 1 : -1);
    }

    /// <summary>A click on the Preview at <paramref name="point"/> (normalized source coordinates of the clip's frame);
    /// <paramref name="additive"/>: Shift was held.</summary>
    public async Task OnClickAsync(NormalizedPoint point, bool additive)
    {
        // A confirm still sampling the colours finishes first: a quick next click never drops it.
        if (!_confirming.IsCompleted) await _confirming;
        if (!IsActive || IsPreparing || _encoded is null || MarkupFrame is null) return;
        if (point.X is < 0 or > 1 || point.Y is < 0 or > 1) return;

        _confirmPending = false;
        if (!(additive && HasCandidate))
        {
            if (!HasCandidate && HitHold(point) is { } hit)
            {
                Select(hit);
                return;
            }
            Select(null);
            _points.Clear();
        }
        _points.Add(new PointD(point.X * MarkupFrame.Width, point.Y * MarkupFrame.Height));
        await PredictAsync();
    }

    private async Task PredictAsync()
    {
        var generation = ++_generation;
        var encoded = _encoded!;
        var points = _points.ToList();
        var token = _modeCts?.Token ?? CancellationToken.None;
        _predicting = true;
        RefreshCommands();
        try
        {
            var set = await encoded.PredictAsync(points, token);
            if (generation != _generation || !IsActive) return;
            var visible = HoldCandidateRules.Visible(set);
            if (points.Count > 1 && visible.Length > 1) visible = ImmutableArray.Create(visible[0]);   // one mask, no alternatives
            if (visible.IsEmpty && points.Count > 1)
            {
                // A refinement without an allowed mask: the candidate stays as it was, only the new point goes, and an
                // Enter pressed meanwhile is dropped (it meant the refined outline, which does not exist).
                if (_points.Count == points.Count) _points.RemoveAt(_points.Count - 1);
                _confirmPending = false;
                _status.Report(RefineRejectedMessage);
                return;
            }
            if (visible.IsEmpty)
            {
                ClearCandidate();
                _status.Report(Strings.Holds_NoHoldThere);
                return;
            }
            _maskSet = set;
            _visible = visible;
            _alternative = 0;
            await ShowAlternativeAsync(generation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                // A failed refinement keeps the candidate as it was, without the new point.
                if (points.Count > 1 && _points.Count == points.Count) _points.RemoveAt(_points.Count - 1);
                _confirmPending = false;
                _status.Report(string.Format(Strings.Holds_Failed, ex.Message));
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _predicting = false;
                RefreshCommands();
                // Enter pressed before the candidate appeared: confirm it now (no candidate → forgotten).
                if (_confirmPending)
                {
                    _confirmPending = false;
                    if (HasCandidate) _ = Confirm();
                }
            }
        }
    }

    private async Task ShowAlternativeAsync(int generation)
    {
        if (_maskSet is null || _visible.IsEmpty) return;
        var mask = _maskSet.Masks[_visible[_alternative]];
        var (component, contour) = await Task.Run(() =>
        {
            var (c, _) = MaskContour.LargestComponent(mask.Pixels, mask.Width, mask.Height);
            return (c, MaskContour.Outer(c, mask.Width, mask.Height));
        });
        if (generation != _generation || !IsActive) return;
        _componentMask = component;
        CandidateContour = contour;
        HasCandidate = contour.Length >= 3;
        AlternativeText = _visible.Length > 1 ? string.Format(Strings.Holds_OutlineOf, _alternative + 1, _visible.Length) : null;
        RefreshCommands();
    }

    private void CycleAlternative(int step)
    {
        if (!HasCandidate || _visible.Length < 2 || !_confirming.IsCompleted) return;
        _alternative = (_alternative + step + _visible.Length) % _visible.Length;
        _ = ShowAlternativeAsync(++_generation);
    }

    private bool CanConfirm() => IsActive && !IsPreparing && (HasCandidate || _predicting);

    /// <summary>Enter: the candidate becomes a hold of the clip — one undo step with its contour and colour samples.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task Confirm()
    {
        if (!_confirming.IsCompleted) return _confirming;
        if (_predicting)
        {
            _confirmPending = true;   // applied when the prediction finishes
            return Task.CompletedTask;
        }
        return _confirming = ConfirmCore();
    }

    private async Task ConfirmCore()
    {
        try
        {
            await AddCandidate();
        }
        catch (Exception ex)
        {
            _status.Report(string.Format(Strings.Holds_AddFailed, ex.Message));
        }
    }

    private async Task AddCandidate()
    {
        if (!CanConfirm() || !HasCandidate || _clip is null || MarkupFrame is not { } frame || _componentMask is not { } component) return;
        var clip = _clip;
        var contour = CandidateContour;
        var others = (clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty).Select(h => (IReadOnlyList<NormalizedPoint>)h.Points).ToList();
        var generation = ++_generation;
        var (surface, wall) = await Task.Run(() => HoldColorSampler.Sample(frame, component, others));
        if (generation != _generation || !IsActive) return;

        var result = _edit.AddHold(clip.Id, new Hold(Guid.NewGuid(), contour, surface, wall, _sourceTime));
        if (!result.Success)
        {
            _status.Report(result.Message ?? Strings.Holds_NotAdded);
            return;
        }
        ClearCandidate();
        _status.Report(string.Format(Strings.Holds_Added, clip.HoldMarkup?.Holds.Length ?? 0));
    }

    private bool CanDeleteSelected() => IsActive && SelectedHoldId is not null;

    /// <summary>Delete: removes the selected hold (one undo step). Never anything on the timeline.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (_clip is null || SelectedHoldId is not { } id) return;
        if (HoveredHandle is { } handle && CanEditContour)
        {
            DeleteVertex(handle);   // Delete over a handle removes that vertex, not the hold (D035)
            return;
        }
        var result = _edit.RemoveHold(_clip.Id, id);
        if (!result.Success) _status.Report(result.Message ?? Strings.Holds_NotRemoved);
        Select(null);
    }

    /// <summary>The shell tells: the playhead moved. A move away from the markup frame leaves the mode.</summary>
    public void OnPlayheadMoved()
    {
        if (IsActive && Playhead() != _modePlayhead) Exit();
        RefreshCommands();
    }

    /// <summary>The shell tells: playback started or the Preview switched to Source — the mode ends.</summary>
    public void OnPreviewStateChanged()
    {
        if (IsActive && !IsPausedOnTimeline()) Exit();
        RefreshCommands();
    }

    /// <summary>Leaves the markup mode: the candidate is discarded, preparation cancelled; the markup stays.</summary>
    public void Exit()
    {
        _generation++;
        _predicting = false;
        _confirmPending = false;
        _modeCts?.Cancel();
        _modeCts = null;
        _encoded?.Dispose();
        _encoded = null;
        _points.Clear();
        _maskSet = null;
        _visible = ImmutableArray<int>.Empty;
        _componentMask = null;
        CandidateContour = ImmutableArray<NormalizedPoint>.Empty;
        HasCandidate = false;
        AlternativeText = null;
        SelectedHoldId = null;
        SelectedContour = ImmutableArray<NormalizedPoint>.Empty;
        SelectedHandles = ImmutableArray<int>.Empty;
        AnimatedContours = ImmutableArray<ImmutableArray<NormalizedPoint>>.Empty;
        _drag = null;
        IsContourInvalid = false;
        HoveredHandle = null;
        MarkupFrame = null;
        IsPreparing = false;
        ModeText = null;
        IsActive = false;
        RefreshCommands();
    }

    private void ClearCandidate()
    {
        _generation++;
        _predicting = false;
        _confirmPending = false;
        _points.Clear();
        _maskSet = null;
        _visible = ImmutableArray<int>.Empty;
        _componentMask = null;
        CandidateContour = ImmutableArray<NormalizedPoint>.Empty;
        HasCandidate = false;
        AlternativeText = null;
        RefreshCommands();
    }

    private void Select(Guid? id)
    {
        _drag = null;
        IsContourInvalid = false;
        HoveredHandle = null;
        SelectedHoldId = id;
        SelectedContour = id is { } h && _clip?.HoldMarkup?.Holds.FirstOrDefault(x => x.Id == h) is { } hold
            ? hold.Points
            : ImmutableArray<NormalizedPoint>.Empty;
        SelectedHandles = SelectedContour.Length >= 3 && MarkupFrame is { } frame
            ? ContourEditing.Handles(SelectedContour, frame.Width, frame.Height, _handleSpacing)
            : ImmutableArray<int>.Empty;
        if (IsActive && !IsPreparing) ModeText = id is null ? ReadyText : SelectedText;
        RefreshCommands();
    }

    // ---- contour correction (D035) --------------------------------------------------------------------------------

    private ContourEdit? _drag;
    private int _dragHandle;
    private double _handleSpacing;

    /// <summary>The least distance between handles in frame pixels, set by the overlay from a fixed spacing on the screen,
    /// so a hold that is small in the Preview shows fewer handles (the geometry keeps every vertex). A change of more than a
    /// quarter re-derives the selected hold's handles (not during a drag).</summary>
    public void SetHandleSpacing(double framePixels)
    {
        if (!double.IsFinite(framePixels) || framePixels <= 0) return;
        if (_handleSpacing > 0 && Math.Abs(framePixels - _handleSpacing) <= 0.25 * _handleSpacing) return;
        _handleSpacing = framePixels;
        if (SelectedHoldId is { } id && !IsDraggingVertex) Select(id);
    }

    /// <summary>The handle under the pointer (an index into <see cref="SelectedHandles"/>), set by the overlay: Delete
    /// removes that vertex instead of the hold.</summary>
    public int? HoveredHandle { get; set; }

    public bool IsDraggingVertex => _drag is not null;

    /// <summary>The selected hold's contour can be corrected now: the mode ready, no candidate, no confirm running, no
    /// export.</summary>
    public bool CanEditContour => IsActive && !IsPreparing && SelectedHoldId is not null && !HasCandidate &&
                                  _confirming.IsCompleted && MarkupFrame is not null && !SelectedHandles.IsEmpty && !_lock.IsLocked;

    /// <summary>A drag of handle <paramref name="handle"/> starts; nothing changes until <see cref="EndVertexDrag"/>.</summary>
    public bool BeginVertexDrag(int handle)
    {
        if (!CanEditContour || handle < 0 || handle >= SelectedHandles.Length) return false;
        _drag = new ContourEdit(SelectedContour, SelectedHandles);
        _dragHandle = handle;
        IsContourInvalid = false;
        return true;
    }

    /// <summary>The dragged handle is at <paramref name="point"/> (normalized source coordinates, clamped to the frame):
    /// the overlay shows the contour as it would become.</summary>
    public void DragVertex(NormalizedPoint point)
    {
        if (_drag is null || MarkupFrame is not { } frame) return;
        var moved = ContourEditing.MoveHandle(_drag, _dragHandle, point, frame.Width, frame.Height);
        SelectedContour = moved.Points;
        IsContourInvalid = ContourEditing.IntroducesCrossing(_drag.Points, moved.Points);
    }

    /// <summary>The drag ends: one undo step — or the contour goes back when it would cross itself.</summary>
    public void EndVertexDrag()
    {
        if (_drag is not { } start) return;
        _drag = null;
        var edited = new ContourEdit(SelectedContour, start.Handles);
        if (IsContourInvalid)
        {
            Restore(start);
            _status.Report(ContourEditing.CrossingMessage);
            return;
        }
        Commit(start, edited);
    }

    /// <summary>The drag is abandoned (Esc, a lost pointer): the contour as it was, nothing changed.</summary>
    public void CancelVertexDrag()
    {
        if (_drag is not { } start) return;
        _drag = null;
        Restore(start);
    }

    /// <summary>Alt+click on edge <paramref name="edge"/> of the selected contour at <paramref name="t"/> along it: a new
    /// vertex (a handle) there — one undo step; on a vertex of a dense contour that vertex becomes a handle (no step).</summary>
    public void InsertVertex(int edge, double t)
    {
        if (!CanEditContour || IsDraggingVertex || MarkupFrame is not { } frame) return;
        var current = new ContourEdit(SelectedContour, SelectedHandles);
        if (edge < 0 || edge >= current.Points.Length) return;
        if (ContourEditing.InsertVertex(current, edge, t, frame.Width, frame.Height) is not { } inserted)
        {
            if (current.Points.Length >= HoldMarkupRules.MaxPoints) _status.Report(string.Format(Strings.Holds_MaxPoints, HoldMarkupRules.MaxPoints));
            return;   // otherwise a handle is there already
        }
        if (inserted.Points == current.Points)
        {
            SelectedHandles = inserted.Handles;   // a hidden vertex of a dense contour shown as a handle: the geometry unchanged
            return;
        }
        Commit(current, inserted);
    }

    public static string OffOutlineMessage => Strings.Holds_OffOutline;

    /// <summary>Alt+click away from the selected outline: nothing changes, the user is told where to click.</summary>
    public void ReportOffOutline()
    {
        if (CanEditContour) _status.Report(OffOutlineMessage);
    }

    /// <summary>Removes handle <paramref name="handle"/> (Delete over it, a double-click on it): one undo step; refused when
    /// fewer than three would stay.</summary>
    public void DeleteVertex(int handle)
    {
        if (!CanEditContour || IsDraggingVertex || handle < 0 || handle >= SelectedHandles.Length) return;
        var current = new ContourEdit(SelectedContour, SelectedHandles);
        if (ContourEditing.RemoveHandle(current, handle) is not { } removed)
        {
            _status.Report(ContourEditing.TooFewPointsMessage);
            return;
        }
        HoveredHandle = null;
        Commit(current, removed);
    }

    /// <summary>Applies <paramref name="edited"/> to the selected hold as one step; on a refusal the contour goes back to
    /// <paramref name="before"/>. The handles are set first, so the notification of the step keeps them (an inserted
    /// vertex stays a handle).</summary>
    private void Commit(ContourEdit before, ContourEdit edited)
    {
        if (_clip is null || SelectedHoldId is not { } id) return;
        if (_lock.IsLocked)
        {
            Restore(before);
            _status.Report(Strings.Holds_OutlineExportRunning);
            return;
        }
        SelectedContour = edited.Points;
        SelectedHandles = edited.Handles;
        IsContourInvalid = false;
        var result = _edit.ReplaceHoldContour(_clip.Id, id, edited.Points);
        if (!result.Success)
        {
            Restore(before);
            _status.Report(result.Message ?? Strings.Holds_OutlineNotChanged);
        }
        else if (_clip.HoldMarkup?.Holds.FirstOrDefault(h => h.Id == id)?.Points != edited.Points)
        {
            Restore(before);   // unchanged: no step
        }
    }

    private void Restore(ContourEdit edit)
    {
        SelectedContour = edit.Points;
        SelectedHandles = edit.Handles;
        IsContourInvalid = false;
    }

    /// <summary>The confirmed hold whose contour contains <paramref name="p"/> (the topmost: the last confirmed).</summary>
    private Guid? HitHold(NormalizedPoint p)
    {
        var holds = _clip?.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty;
        for (var i = holds.Length - 1; i >= 0; i--)
            if (Contains(holds[i].Points, p)) return holds[i].Id;
        return null;
    }

    private static bool Contains(ImmutableArray<NormalizedPoint> polygon, NormalizedPoint p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Where the clip's picture lies on the canvas now (D018) — for the overlay's mapping of clicks — and the
    /// selected hold's contour after an undo / redo.</summary>
    private void UpdateOverlay()
    {
        var settings = _projects.Current.Settings;
        Canvas = new FrameSize(settings.FrameWidth, settings.FrameHeight);
        Geometry = _clip is not null && MarkupFrame is { } frame && VisualProperties.Of(_clip) is { } visual
            ? CompositionMath.Layout(Canvas, new FrameSize(frame.Width, frame.Height), visual)
            : null;
        if (SelectedHoldId is not { } id) return;
        if (_clip?.HoldMarkup?.Holds.FirstOrDefault(h => h.Id == id) is not { } hold)
        {
            Select(null);
            return;
        }
        // The selected hold's contour changed elsewhere (undo / redo): show it with fresh handles; a drag is abandoned.
        var shown = _drag?.Points ?? SelectedContour;
        if (hold.Points != shown) Select(id);
    }

    private MediaAsset? Asset(MediaBackedClip clip) => _projects.Current.MediaAssets.FirstOrDefault(a => a.Id == clip.MediaAssetId);

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        SetStartToPlayheadCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
