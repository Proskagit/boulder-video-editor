using AiVideoEditor.Core.Entities;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- Unlink Audio, Link, Unlink from the timeline toolbar (Phase 17, D032 R3, R4, AQ9) --------------------------------
// One button unlinks: a linked selection loses its links (Unlink), a video clip with sound gets its sound as an audio clip
// (Unlink Audio); Link joins the selected video and audio clip. The rules, refusals and messages are the edit service's.
public sealed partial class TimelineViewModel
{
    /// <summary>The unlink button's label: "Unlink" while the selection holds a linked clip, else "Unlink Audio".</summary>
    public string UnlinkLabel => SelectedClips().Any(IsLinked) ? "Unlink" : "Unlink Audio";

    private bool CanUnlink()
    {
        if (!CanEdit()) return false;
        var clips = SelectedClips();
        if (clips.Any(IsLinked)) return true;
        return _selection.Count > 0 && _edit.GetUnlinkAudioBlockReason(_selection[^1]) is null;
    }

    /// <summary>Unlink (D032 R4) or Unlink Audio (R3) — see <see cref="UnlinkLabel"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlink))]
    private void Unlink()
    {
        if (SelectedClips().Any(IsLinked))
        {
            Report(_edit.UnlinkClips(_selection.ToList()));
            return;
        }
        if (_selection.Count == 0) return;
        var result = _edit.UnlinkAudio(_selection[^1]);
        if (result.Success) SelectAdded(result);
        Report(result);
    }

    private bool CanLink()
    {
        if (!CanEdit()) return false;
        var clips = SelectedClips();
        return clips.Count == 2 && clips.OfType<VideoClip>().Count() == 1 && clips.OfType<AudioClip>().Count() == 1 && !clips.Any(IsLinked);
    }

    /// <summary>Link (D032 R4, AQ9): the selected video clip and audio clip become one link.</summary>
    [RelayCommand(CanExecute = nameof(CanLink))]
    private void Link()
    {
        var result = _edit.LinkClips(_selection.ToList());
        if (result.Success) SelectAdded(result);
        Report(result);
    }

    private List<Clip> SelectedClips() =>
        _selection.Select(id => _clipViewModels.TryGetValue(id, out var vm) ? vm.Clip : null).OfType<Clip>().ToList();

    private static bool IsLinked(Clip clip) => clip is MediaBackedClip { LinkId: not null };

    private void NotifyLinkCommands()
    {
        OnPropertyChanged(nameof(UnlinkLabel));
        UnlinkCommand.NotifyCanExecuteChanged();
        LinkCommand.NotifyCanExecuteChanged();
    }
}
