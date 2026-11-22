using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Implements <see cref="ITimelineEditService"/>. Each operation builds an
/// <see cref="EditPlan"/> in frame indices, validates every affected track with
/// <see cref="TimelineValidator"/>, and only then executes one undoable command.
/// Must be called on the UI thread (the project model is not thread-safe).
/// </summary>
public sealed partial class TimelineEditService : ITimelineEditService
{
    /// <summary>Length of a newly added image clip.</summary>
    public static readonly MediaTime DefaultImageDuration = MediaTime.FromSeconds(5);

    /// <summary>Length of a newly added text clip.</summary>
    public static readonly MediaTime DefaultTextDuration = MediaTime.FromSeconds(5);

    /// <summary>Content of a newly added text clip (empty text would not be visible).</summary>
    public static string DefaultText => Messages.Text_Default;

    /// <summary>Source frame rates outside this range are treated as unknown.</summary>
    public const int MinSourceFps = 1;
    public const int MaxSourceFps = 240;

    private readonly IProjectService _projectService;
    private readonly IUndoRedoService _undoRedo;
    private readonly ILogger<TimelineEditService> _logger;

    /// <param name="startCorrection">The current estimate frozen into new FIT marks (D051 §5.5); <c>I</c> without one.</param>
    /// <param name="time">The clock of the marks' <c>positionChangedAt</c> (D051 §5.6).</param>
    public TimelineEditService(IProjectService projectService, IUndoRedoService undoRedo, ILogger<TimelineEditService> logger,
        IStartCorrectionSource? startCorrection = null, TimeProvider? time = null)
    {
        _projectService = projectService;
        _undoRedo = undoRedo;
        _logger = logger;
        _startCorrection = startCorrection;
        _time = time ?? TimeProvider.System;
    }

    private readonly IStartCorrectionSource? _startCorrection;
    private readonly TimeProvider _time;

    private Core.Entities.Project Project => _projectService.Current;
    private Sequence Sequence => Project.Timeline;
    private ProjectSettings Settings => Project.Settings;

    public FrameRate FrameRate => Settings.FrameRate;

    // --- Snapping --------------------------------------------------------------

    public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds,
        Func<SnapResult, bool>? accept = null)
    {
        var excluded = excludedClipIds as ISet<Guid> ?? excludedClipIds.ToHashSet();
        var targets = new List<MediaTime> { MediaTime.Zero, Sequence.PlayheadPosition };
        targets.AddRange(Sequence.Markers.Select(m => m.Position));   // D027 §6
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            foreach (var clip in track.Clips)
            {
                if (excluded.Contains(clip.Id)) continue;
                targets.Add(clip.TimelineStart);
                targets.Add(clip.TimelineEnd);
            }
        }

        return accept is null
            ? SnapEngine.Find(candidates, targets, tolerance)
            : SnapEngine.FindAll(candidates, targets, tolerance).FirstOrDefault(accept);
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>The asset's rate if ffprobe reported a usable one, else the 30 FPS
    /// fallback — with a flag so the caller never presents the fallback as the
    /// file's real rate.</summary>
    public static (FrameRate Rate, bool FromSource) ResolveSourceFrameRate(MediaAsset asset)
    {
        if (asset.Metadata?.FrameRate is { IsValid: true } rate &&
            rate.Numerator >= (long)MinSourceFps * rate.Denominator &&
            rate.Numerator <= (long)MaxSourceFps * rate.Denominator)
        {
            return (rate, true);
        }

        return (FrameRate.Default, false);
    }

    public static string FormatRate(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : Math.Round(rate.ToDouble(), 3).ToString("0.###", CultureInfo.InvariantCulture);

    private MediaAsset? FindAsset(Guid id) => Project.MediaAssets.FirstOrDefault(a => a.Id == id);

    /// <summary>D064 amendment 1: the video clip plays its own sound (not unlinked; its media has an audio stream).</summary>
    private bool HasOwnSound(VideoClip clip) => !clip.IsAudioDetached && FindAsset(clip.MediaAssetId)?.Metadata?.AudioCodec is not null;

    private Track? FindTrack(Guid id) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.Id == id);

    /// <summary>Source length limit for video/audio clips; null for clips without one (images, text).</summary>
    private MediaTime? SourceDuration(Clip clip) =>
        clip is VideoClip or AudioClip ? FindAsset(((MediaBackedClip)clip).MediaAssetId)?.Metadata?.Duration : null;

    private static string? Resolve(EditPlan plan, IEnumerable<Guid> ids, out List<Clip> clips)
    {
        var byId = plan.AllTracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id);
        clips = new List<Clip>();
        foreach (var id in ids.Distinct())
        {
            if (!byId.TryGetValue(id, out var clip))
                return Messages.Edit_SelectedClipGone;
            clips.Add(clip);
        }
        return null;
    }

    private static string? CheckEditable(EditPlan plan, IEnumerable<Clip> clips)
    {
        foreach (var clip in clips)
        {
            if (plan.TrackOf(clip).IsLocked)
                return string.Format(Messages.Edit_TrackLocked, plan.TrackOf(clip).Name);
        }
        return null;
    }

    /// <summary>Validates the plan: the dissolves are first brought in line with the planned clips (D025 §5: removed
    /// where their cut is gone, moved with their clips) and so are the links (D032 R5: a link left without its other clip
    /// is cleared); then every affected track's clips and dissolves are checked — zones against the clips, and the source
    /// handles of every dissolve the edit creates or keeps (§4) — and the links.</summary>
    private string? Validate(EditPlan plan)
    {
        if (ApplyMagnet(plan) is { } magnetError) return magnetError;               // D069 §6: no gaps on the main track
        if (Sequence.MagneticMainTrack) plan.ResetCarried();   // D069 amendment 1: the templates follow where the clips end
        if (ReconcileOverlays(plan) is { } overlayError) return overlayError;   // D048: the overlays follow their clip
        if (PlanPartnerShifts(plan) is { } partnerError) return partnerError;
        plan.ReconcileTransitions();
        plan.ClampFades();
        plan.ReconcileLinks();
        foreach (var track in plan.AffectedTracks)
        {
            if (TimelineValidator.ValidateTrack(track, plan.EffectiveClips(track), plan.Rate, FindAsset) is { } error)
                return error;
        }
        foreach (var track in plan.TransitionTracks)
        {
            if (ValidateTransitions(plan, track) is { } error)
                return error;
        }
        // D044 §4: the fill peaks of every video track as the plan leaves them — rejected, never clamped.
        if (ValidateFillPeaks(plan) is { } peaksError) return peaksError;
        // A link the edit leaves without its other clip is cleared — never on a locked track (D032 L5, AQ12).
        foreach (var clip in plan.ClearedLinks)
        {
            if (plan.TrackOf(clip).IsLocked)
                return string.Format(Messages.Edit_TrackLocked, plan.TrackOf(clip).Name);
        }
        return LinkRules.Validate(plan.EffectiveLinks());
    }

    public static string NotEnoughMedia => Messages.Transition_NotEnoughMediaDissolve;

    private string? ValidateTransitions(EditPlan plan, Track track)
    {
        var transitions = plan.EffectiveTransitions(track).ToList();
        if (transitions.Count == 0) return null;

        var clips = plan.EffectiveClips(track).ToDictionary(c => c.Clip.Id);
        var specs = transitions.Select(t =>
            new TransitionSpec(t.Transition.Id, t.State.TypeId, t.State.Duration, t.State.LeftClipId, t.State.RightClipId, t.State.Direction));
        if (TransitionRules.Validate(track.Type, track.Name, specs,
                id => clips.TryGetValue(id, out var c) ? (c.State.Start, c.State.End) : null, plan.Rate) is { } error)
            return error;

        foreach (var (transition, state) in transitions)
        {
            if (!plan.IsTouched(transition) || !TransitionRules.NeedsSourceHandles(state.TypeId)) continue;
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(state.Duration, plan.Rate));
            var left = clips[state.LeftClipId];
            var right = clips[state.RightClipId];
            if (afterCut > DissolveHandles.After(left.Clip, left.State, AssetOf(left.Clip), plan.Rate) ||
                beforeCut > DissolveHandles.Before(right.Clip, right.State, AssetOf(right.Clip), plan.Rate))
                return NotEnoughMediaFor(TransitionNoun(track));   // the crossfade only (D069 amendment 2)
        }
        return null;
    }

    /// <summary>D044 §4 on the plan: every video track's transitions with their cut (the left clip's planned end).</summary>
    private static string? ValidateFillPeaks(EditPlan plan)
    {
        var peaks = new List<(string TypeId, MediaTime Cut)>();
        foreach (var track in plan.AllTracks.Where(t => t.Type == TrackType.Video))
        {
            var ends = plan.EffectiveClips(track).ToDictionary(c => c.Clip.Id, c => c.State.End);
            foreach (var (_, state) in plan.EffectiveTransitions(track))
                if (TransitionRules.IsFill(state.TypeId) && ends.TryGetValue(state.LeftClipId, out var cut))
                    peaks.Add((state.TypeId, cut));
        }
        return TransitionRules.ValidateFillPeaks(peaks);
    }

    private MediaAsset? AssetOf(Clip clip) => clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;

    /// <summary>The frames of <paramref name="clip"/> inside the dissolve zones at its start and its end (0 without one).</summary>
    private static (long AtStart, long AtEnd) DissolveParts(Track track, Clip clip, FrameRate rate)
    {
        long atStart = 0, atEnd = 0;
        foreach (var t in track.Transitions)
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
            if (t.RightClipId == clip.Id) atStart = afterCut;
            if (t.LeftClipId == clip.Id) atEnd = beforeCut;
        }
        return (atStart, atEnd);
    }

    /// <summary>The status note for dissolves an edit removed because their cut is gone (D025 §5), or null.</summary>
    private static string? TransitionNote(EditPlan plan) => Join(
        plan.AutoRemovedVideoTypes switch
        {
            [] => null,
            [var one] => IsNamed(one) ? string.Format(Messages.Edit_TransitionRemovedNamed, TypeName(one)) : Messages.Edit_DissolveRemoved,
            var many => many.Any(IsNamed)
                ? string.Format(Messages.Edit_TransitionsRemoved, many.Count)
                : string.Format(Messages.Edit_DissolvesRemoved, many.Count)
        },
        plan.AutoRemovedCrossfades switch
        {
            0 => null,
            1 => Messages.Edit_CrossfadeRemoved,
            var n => string.Format(Messages.Edit_CrossfadesRemoved, n)
        });

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} {b}";

    /// <summary>Executes the plan as one Undo step; false — nothing to do (D069 §6: every change the magnet undid again), no
    /// step recorded.</summary>
    private bool Commit(EditPlan plan, string description)
    {
        plan.DropNoOpUpdates();
        if (plan.IsEmpty) return false;
        var command = new NotifyingCommand(plan.BuildCommand(description), _projectService.NotifyTimelineChanged);
        ExecuteTracked(command);
        return true;
    }

    private static Clip CreateClip(MediaAsset asset, ClipState state)
    {
        Clip clip = asset.Kind switch
        {
            MediaKind.Video => new VideoClip { MediaAssetId = asset.Id },
            MediaKind.Audio => new AudioClip { MediaAssetId = asset.Id },
            _ => new ImageClip { MediaAssetId = asset.Id }
        };
        state.ApplyTo(clip);
        return clip;
    }

    /// <summary>The clipboard's copy of <paramref name="clip"/>: <see cref="CloneClip"/> keeping the link, so a paste can
    /// tell which copied clips belong together (D032 R5). Never inserted itself.</summary>
    private static Clip CopyWithLink(Clip clip)
    {
        var copy = CloneClip(clip);
        if (copy is MediaBackedClip media && clip is MediaBackedClip source) media.LinkId = source.LinkId;
        return copy;
    }

    /// <summary>Copy of every clip property with a new Id (used for the right half of a split). The link is not copied:
    /// whoever makes the copy decides its link (D032 R5).</summary>
    private static Clip CloneClip(Clip source)
    {
        Clip copy = source switch
        {
            VideoClip v => new VideoClip
            {
                MediaAssetId = v.MediaAssetId, Speed = v.Speed, PositionX = v.PositionX, PositionY = v.PositionY,
                Scale = v.Scale, RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted,
                Crop = v.Crop, IsAudioDetached = v.IsAudioDetached,
                // D064 §1: the volume points (source time), the sound-only fades (a split clears the inner edges) and the clean-up.
                Volumes = v.Volumes, SoundFadeIn = v.SoundFadeIn, SoundFadeOut = v.SoundFadeOut, Cleanup = v.Cleanup,
                // D061: Follow (its target is a source time of the same video) and Occlusion go with the copy.
                Follow = v.Follow, HidesOutlinesBehindPeople = v.HidesOutlinesBehindPeople,
                // D063: the transform keyframes (source time, immutable) go with the copy — a split's two parts keep the same
                // curve, so the picture does not change at the cut; each part shows and edits its own.
                Keyframes = v.Keyframes,
                // D034 §6 / D053: the routes go with the copy; they are immutable, so the two clips stay independent.
                Routes = v.Routes,
                Color = v.Color   // D069 §7 (immutable)
            },
            AudioClip a => new AudioClip
            {
                MediaAssetId = a.MediaAssetId, Speed = a.Speed, Volume = a.Volume, IsMuted = a.IsMuted, Volumes = a.Volumes, Cleanup = a.Cleanup
            },
            ImageClip i => new ImageClip
            {
                MediaAssetId = i.MediaAssetId, Speed = i.Speed, PositionX = i.PositionX, PositionY = i.PositionY,
                Scale = i.Scale, RotationDegrees = i.RotationDegrees, Opacity = i.Opacity, Crop = i.Crop
            },
            TextClip t => new TextClip
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, ColorHex = t.ColorHex, Alignment = t.Alignment,
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity,
                // D043: the style and both motions go with the copy (a split's parts keep them; their fades follow D025).
                Bold = t.Bold, Outline = t.Outline, Background = t.Background, MotionIn = t.MotionIn, MotionOut = t.MotionOut,
                Italic = t.Italic, Shadow = t.Shadow, LineSpacing = t.LineSpacing, BoxWidth = t.BoxWidth
            },
            TemplateClip p => new TemplateClip
            {
                Kind = p.Kind, CounterStart = p.CounterStart, Date = p.Date, Route = p.Route, Grade = p.Grade, Status = p.Status,
                AccentHex = p.AccentHex, Title = p.Title, Subtitle = p.Subtitle, StatusAt = p.StatusAt, Look = p.Look, Corner = p.Corner,
                ShowHeartRate = p.ShowHeartRate, HeartHex = p.HeartHex, PlateCorner = p.PlateCorner,
                Attempts = p.Attempts, Language = p.Language, OwnBackground = p.OwnBackground,   // D073
                // D048 §3.5: kept by a split's right part (re-anchored by the split); remapped or cleared by a paste. D053: the
                // route and the FIT climb go with it (cleared on a copy).
                AnchorClipId = p.AnchorClipId, AnchorRouteId = p.AnchorRouteId, FitClimb = p.FitClimb, FitLayout = p.FitLayout, RouteStart = p.RouteStart
            },
            _ => throw new NotSupportedException($"Unknown clip type {source.GetType().Name}.")
        };

        ClipState.Capture(source).ApplyTo(copy);
        (copy.FadeIn, copy.FadeOut) = (source.FadeIn, source.FadeOut);
        foreach (var effect in source.Effects)
        {
            copy.Effects.Add(new Effect
            {
                EffectTypeId = effect.EffectTypeId,
                DisplayName = effect.DisplayName,
                IsEnabled = effect.IsEnabled,
                Parameters = new Dictionary<string, object?>(effect.Parameters)
            });
        }
        return copy;
    }
}
