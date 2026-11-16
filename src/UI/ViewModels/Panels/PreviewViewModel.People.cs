using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A person drawn by the Preview's people overlay: its id, its box on the shown frame (normalized source
/// coordinates) and whether it is the one Follow follows.</summary>
public readonly record struct PersonMarker(int Id, NormalizedBox Box, bool IsFollowed);

/// <summary>
/// What the Preview's people overlay draws over one shown picture (D061 §7): the layer's geometry on that frame (Follow
/// included), the people's boxes while the user picks the climber, the people's separate masks while they are shown. Never
/// exported — the export draws only the composition.
/// </summary>
public sealed record PeopleOverlayState(Guid ClipId, FrameSize Canvas, LayerGeometry Geometry, MediaTime SourceTime, bool IsPicking,
    ImmutableArray<PersonMarker> People, IReadOnlyList<(int PersonId, OcclusionMask Mask)> Masks);

/// <summary>D061 in the Preview: the people analyses in the snapshot, the notices, the pick of the climber and the masks.</summary>
public sealed partial class PreviewViewModel
{
    private PeopleAnalysisCoordinator? _people;
    private Guid? _pickClipId;

    /// <summary>D061: what the Preview says about Follow / Occlusion on the shown frame (the climber not seen here, the analysis
    /// running); null when nothing is to say.</summary>
    [ObservableProperty] private string? _peopleNotice;

    /// <summary>D061: the people overlay's content; null — nothing drawn.</summary>
    [ObservableProperty] private PeopleOverlayState? _peopleOverlay;

    /// <summary>D061: the people's masks are shown over the picture (a check of the segmentation; session only).</summary>
    [ObservableProperty] private bool _showPeopleMasks;

    partial void OnShowPeopleMasksChanged(bool value) => UpdatePeopleOverlay();

    /// <summary>The user picked a person for the clip's Follow: the clip, the source time of the shown frame and the point.</summary>
    public event EventHandler<(Guid ClipId, MediaTime SourceTime, NormalizedPoint Point)>? PersonPicked;

    /// <summary>The pick ended (picked or cancelled).</summary>
    public event EventHandler? PersonPickEnded;

    public bool IsPickingPerson => _pickClipId is not null;

    private void InitPeople(PeopleAnalysisCoordinator? people)
    {
        _people = people;
        if (_people is null) return;
        _people.AnalysesChanged += OnPeopleAnalysesChanged;
        _people.StatusChanged += OnPeopleStatusChanged;
    }

    private void ReleasePeople()
    {
        if (_people is null) return;
        _people.AnalysesChanged -= OnPeopleAnalysesChanged;
        _people.StatusChanged -= OnPeopleStatusChanged;
    }

    /// <summary>A finished analysis moves the picture (Follow) or hides outlines (Occlusion) — a presentation-only change.</summary>
    private void OnPeopleAnalysesChanged(object? sender, EventArgs e)
    {
        if (!_released && !IsSourceMode) RebuildSnapshot();
        UpdatePeopleOverlay();
    }

    private void OnPeopleStatusChanged(object? sender, EventArgs e)
    {
        if (!_released) UpdatePeopleNotice();
    }

    /// <summary>Starts the pick of the climber on <paramref name="clipId"/>'s shown frame: the people's boxes are drawn, a
    /// click on one picks it (<see cref="PickAt"/>), Esc or another pick cancels.</summary>
    public void StartPersonPick(Guid clipId)
    {
        _pickClipId = clipId;
        OnPropertyChanged(nameof(IsPickingPerson));
        UpdatePeopleOverlay();
    }

    public void CancelPersonPick()
    {
        if (_pickClipId is null) return;
        _pickClipId = null;
        OnPropertyChanged(nameof(IsPickingPerson));
        UpdatePeopleOverlay();
        PersonPickEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A click at <paramref name="point"/> (normalized source coordinates of the shown frame) while picking: the
    /// person there becomes the clip's climber; a click on nobody keeps the pick open. True when someone was picked.</summary>
    public bool PickAt(NormalizedPoint point)
    {
        if (PeopleOverlay is not { IsPicking: true } overlay || _pickClipId != overlay.ClipId) return false;
        var hit = overlay.People.Where(p => p.Box.Contains(point)).OrderBy(p => p.Box.Area).FirstOrDefault();
        if (hit.Box.Area <= 0) return false;
        var clipId = overlay.ClipId;
        _pickClipId = null;
        OnPropertyChanged(nameof(IsPickingPerson));
        PersonPicked?.Invoke(this, (clipId, overlay.SourceTime, point));
        PersonPickEnded?.Invoke(this, EventArgs.Empty);
        UpdatePeopleOverlay();
        return true;
    }

    /// <summary>The overlay for the shown frame: the picked clip's people while picking, the masks while they are shown.</summary>
    private void UpdatePeopleOverlay()
    {
        PeopleOverlayState? state = null;
        if (_people is not null && !IsSourceMode && (_pickClipId is not null || ShowPeopleMasks))
        {
            foreach (var picture in Layers.Reverse())
            {
                if (picture.Layer is not PictureLayer layer || picture.Frame is not { } frame) continue;
                if (_pickClipId is { } pick && layer.ClipId != pick) continue;
                var analyses = _people.Analyses;
                analyses.Tracks.TryGetValue(layer.Span.AssetId, out var tracks);
                analyses.Masks.TryGetValue(layer.Span.AssetId, out var masks);
                if (tracks is null && masks is null) continue;
                var time = CameraMotionMath.SourceTimeOf(frame.Timestamp, layer.Span.SourceStartTime);
                var geometry = CompositionDrawPlan.GeometryAt(Canvas, layer, new FrameSize(frame.Width, frame.Height), frame.Timestamp);
                var followed = FollowedTrack(layer.ClipId, tracks);
                var people = tracks?.SampleAt(time) is { } sample
                    ? sample.People.Select((p, i) => new PersonMarker(sample.TrackIdOf(i), p.Box, followed is { } f && f.Contains(sample.TrackIdOf(i))))
                        .Where(p => p.Id >= 0).ToImmutableArray()
                    : ImmutableArray<PersonMarker>.Empty;
                state = new PeopleOverlayState(layer.ClipId, Canvas, geometry, time, _pickClipId is not null, people,
                    ShowPeopleMasks && masks is not null ? masks.PeopleAt(time) : Array.Empty<(int, OcclusionMask)>());
                break;
            }
        }
        PeopleOverlay = state;
        UpdatePeopleNotice();
    }

    /// <summary>The tracks of the person a clip follows (null: no Follow or not resolved).</summary>
    private ImmutableArray<int>? FollowedTrack(Guid clipId, PersonTracks? tracks)
    {
        if (tracks is null) return null;
        var clip = _projectService.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().FirstOrDefault(c => c.Id == clipId);
        if (clip?.Follow is not { } follow) return null;
        var asset = _projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == clip.MediaAssetId);
        if (PlaybackSnapshotBuilder.DisplaySize(asset?.Metadata) is not { } source) return null;
        return Resolution(clip, follow, tracks, source).Trajectory?.TrackIds;
    }

    private (VideoClip Clip, FollowSettings Follow, VisualProperties? Visual, MediaTime In, MediaTime Out, PersonTracks Tracks, FrameSize Canvas,
        FrameSize Source, FollowResolution Result)? _resolution;

    /// <summary>The clip's Follow resolved (the last one cached: the notice asks on every shown frame).</summary>
    private FollowResolution Resolution(VideoClip clip, FollowSettings follow, PersonTracks tracks, FrameSize source)
    {
        var visual = VisualProperties.Of(clip);
        if (_resolution is { } r && ReferenceEquals(r.Clip, clip) && r.Follow == follow && r.Visual == visual && r.In == clip.SourceIn &&
            r.Out == clip.SourceOut && ReferenceEquals(r.Tracks, tracks) && r.Canvas == Canvas && r.Source == source)
            return r.Result;
        var result = FollowResolution.For(clip, follow, tracks, Canvas, source);
        _resolution = (clip, follow, visual, clip.SourceIn, clip.SourceOut, tracks, Canvas, source, result);
        return result;
    }

    /// <summary>The notice over the picture (D061 §7): for the shown clips with Follow or Occlusion — the climber is not seen
    /// on this frame (the camera holds), the analysis still runs (with its progress); null otherwise.</summary>
    private void UpdatePeopleNotice()
    {
        string? notice = null;
        if (_people is not null && !IsSourceMode)
        {
            if (_pickClipId is not null) notice = Strings.Preview_PickPerson;
            foreach (var picture in Layers)
            {
                if (notice is not null) break;
                if (picture.Layer is not PictureLayer layer) continue;
                var clip = _projectService.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().FirstOrDefault(c => c.Id == layer.ClipId);
                if (clip is null || (clip.Follow is null && !clip.HidesOutlinesBehindPeople)) continue;
                var status = _people.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Tracks);
                if (status.State == PeopleAnalysisState.Ready && clip.HidesOutlinesBehindPeople)
                    status = _people.StatusOf(clip.MediaAssetId, PeopleAnalysisKind.Masks);
                if (status.State == PeopleAnalysisState.Analysing)
                    notice = string.Format(CultureInfo.CurrentCulture, Strings.Preview_PeopleAnalysing, (int)Math.Floor(status.Progress * 100));
                else if (status.State == PeopleAnalysisState.Waiting)
                    notice = string.Format(CultureInfo.CurrentCulture, Strings.Preview_PeopleAnalysing, 0);
                else if (clip.Follow is not null && layer.Span.Follow is not null && picture.Frame is { } frame &&
                         _people.Analyses.Tracks.TryGetValue(clip.MediaAssetId, out var tracks) &&
                         PlaybackSnapshotBuilder.DisplaySize(_projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == clip.MediaAssetId)?.Metadata) is { } source &&
                         Resolution(clip, clip.Follow, tracks, source).Trajectory is { } trajectory &&
                         !trajectory.IsSeenAt(CameraMotionMath.SourceTimeOf(frame.Timestamp, layer.Span.SourceStartTime)))
                    notice = Strings.Preview_FollowLostHere;
            }
        }
        PeopleNotice = notice;
    }
}
