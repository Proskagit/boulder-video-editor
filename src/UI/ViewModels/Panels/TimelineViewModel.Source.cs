using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- Insert / Overwrite from the Source viewer (Phase 16, D031 SQ5 / SQ8 / SQ10 / SQ16) ---------------------------------
public sealed partial class TimelineViewModel
{
    private readonly SourceViewerService? _source;

    private bool CanPlaceSource() => CanEdit() && _source?.Asset is not null;

    /// <summary><c>,</c> and the Insert button: the source range (or the whole asset) at the playhead, the target track's
    /// later clips moved right.</summary>
    [RelayCommand(CanExecute = nameof(CanPlaceSource))]
    private void InsertFromSource() => PlaceSource(overwrite: false);

    /// <summary><c>.</c> and the Overwrite button: the source range at the playhead over what is there.</summary>
    [RelayCommand(CanExecute = nameof(CanPlaceSource))]
    private void OverwriteFromSource() => PlaceSource(overwrite: true);

    private void PlaceSource(bool overwrite)
    {
        if (_source?.Asset is not { } asset || _source.Selection is not { } range) return;
        var target = SourceTargetTrack(asset);
        var result = overwrite
            ? _edit.OverwriteClip(asset.Id, range.In, range.Out, Playhead, target?.Id)
            : _edit.InsertClip(asset.Id, range.In, range.Out, Playhead, target?.Id);
        if (result.Success)
        {
            // SQ10: the new clip is the selection and the playhead goes to its end, ready for the next one.
            SelectAdded(result);
            var clip = Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).First(c => c.Id == result.ClipIds[0]);
            SetPlayhead(clip.TimelineEnd);
        }
        Report(result, successMessage: overwrite ? $"Overwrote with {asset.FileName}" : $"Inserted {asset.FileName}");
    }

    /// <summary>SQ8: the track of the latest selected clip on a track of the source's kind (the selection's last entry is
    /// the primary); null without one — the edit service then takes the first track of that kind (V1 / A1).</summary>
    private Track? SourceTargetTrack(MediaAsset asset)
    {
        var type = asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video;
        for (var i = _selection.Count - 1; i >= 0; i--)
        {
            var id = _selection[i];
            var track = Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.Clips.Any(c => c.Id == id));
            if (track?.Type == type) return track;
        }
        return null;
    }

    private void NotifySourceCommands()
    {
        InsertFromSourceCommand.NotifyCanExecuteChanged();
        OverwriteFromSourceCommand.NotifyCanExecuteChanged();
    }
}
