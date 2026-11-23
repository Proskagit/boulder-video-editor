using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- Vertical video and framing (Phase 26, D041) ------------------------------------------------------------------------
public sealed partial class TimelineViewModel
{
    /// <summary>The last canvas offer (finished when no dialog is open) — for tests.</summary>
    internal Task CanvasOffer { get; private set; } = Task.CompletedTask;

    private (int Width, int Height) CurrentCanvas =>
        (_projectService.Current.Settings.FrameWidth, _projectService.Current.Settings.FrameHeight);

    private bool IsTimelineEmpty => Sequence.VideoTracks.Concat(Sequence.AudioTracks).All(t => t.Clips.Count == 0);

    /// <summary>
    /// D041 §1, changed by D068 amendment 1: after an add to a timeline that was empty, when it created a video clip with its
    /// picture and the asset's orientation and display size are known, ask whether the canvas should take the video's shape
    /// (<see cref="ProjectSettingsRules.SuggestedCanvas"/>) when the canvas has another one. An empty timeline is a fresh
    /// start every time — also after the timeline was cleared, whatever canvas an earlier video gave. Accepting is
    /// <see cref="ITimelineEditService.SetCanvasSize"/> — one undo step after the add's. Never while an offer is open.
    /// </summary>
    private void OfferCanvasAfterAdd(bool timelineWasEmpty, TimelineEditResult result)
    {
        if (!timelineWasEmpty || !result.Success || !CanvasOffer.IsCompleted || _dialogs is null) return;

        var video = Sequence.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().FirstOrDefault(c => result.ClipIds.Contains(c.Id));
        if (video is null || FindAsset(video.MediaAssetId) is not { Metadata: { } metadata } asset) return;
        if (metadata.DisplayRotation is null || metadata is not { DisplayWidth: > 0 and var width, DisplayHeight: > 0 and var height })
            return;
        if (ProjectSettingsRules.SuggestedCanvas(width, height) is not { } offered || offered == CurrentCanvas) return;
        // D078 amendment 3: a canvas of the video's shape already (the first 4K video gave its size) is never offered smaller.
        var canvas = CurrentCanvas;
        if (Math.Abs((long)width * canvas.Height - (long)height * canvas.Width) * 100 <= (long)height * canvas.Width) return;

        CanvasOffer = AskCanvasAsync(asset.FileName, width, height, offered);
    }

    private async Task AskCanvasAsync(string fileName, int width, int height, (int Width, int Height) offered)
    {
        var project = _projectService.Current;
        var current = CurrentCanvas;
        var choice = await _dialogs!.AskAsync(new DialogRequest
        {
            Title = Strings.Canvas_OfferTitle,
            Message = string.Format(Strings.Canvas_OfferMessage, fileName, width, height, current.Width, current.Height,
                offered.Width, offered.Height),
            Buttons = new[] { Strings.Canvas_OfferAccept, Strings.Canvas_OfferKeep }
        });
        // The answer may come after an export started, the project was replaced or its canvas changed.
        if (choice != 0 || !CanEdit() || !ReferenceEquals(project, _projectService.Current) || CurrentCanvas != current) return;

        Report(_edit.SetCanvasSize(offered.Width, offered.Height),
            successMessage: string.Format(Strings.Canvas_OfferApplied, offered.Width, offered.Height));
    }

    // §2: "Fill frame" / "Fit in frame" from the clip's context menu, for the clicked clip.

    private bool CanFrame(TimelineClipViewModel? clip) => CanEdit() && clip?.Clip is VideoClip or ImageClip;

    [RelayCommand(CanExecute = nameof(CanFrame))]
    private void FillFrame(TimelineClipViewModel? clip) => FrameClip(clip, ClipFraming.Fill);

    [RelayCommand(CanExecute = nameof(CanFrame))]
    private void FitFrame(TimelineClipViewModel? clip) => FrameClip(clip, ClipFraming.Fit);

    private void FrameClip(TimelineClipViewModel? clip, ClipFraming framing)
    {
        if (clip is null || !CanEdit()) return;
        // D063 §5: a keyframed clip gets the scale in its keyframe on the playhead's frame.
        var result = _edit.FrameClip(clip.Id, framing, Keyframes.FramingTimeAt(clip.Clip, EditPlayhead.ToFrameFloor(FrameRate)));
        _status.Report(ClipFramingStatus.Text(result, framing, clip.Clip));
    }
}
