using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Left-hand Media Browser. <see cref="Items"/> is mock data for Phase 1 — clearly
/// isolated in the constructor below so it's a one-place swap for the real
/// <c>IMediaImportService</c>-backed collection in Phase 2.
/// </summary>
public sealed partial class MediaBrowserViewModel : ViewModelBase
{
    private readonly ILogger<MediaBrowserViewModel> _logger;

    public ObservableCollection<MediaBrowserItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private MediaBrowserItemViewModel? _selectedItem;

    public MediaBrowserViewModel(ILogger<MediaBrowserViewModel> logger)
    {
        _logger = logger;

        // --- Mock data (Phase 1 only) --------------------------------------
        Items.Add(new MediaBrowserItemViewModel { FileName = "beach_sunset.mp4", DurationDisplay = "00:01:24", Kind = MediaBrowserItemKind.Video });
        Items.Add(new MediaBrowserItemViewModel { FileName = "interview_a.mov", DurationDisplay = "00:04:57", Kind = MediaBrowserItemKind.Video });
        Items.Add(new MediaBrowserItemViewModel { FileName = "background_music.mp3", DurationDisplay = "00:03:12", Kind = MediaBrowserItemKind.Audio });
        Items.Add(new MediaBrowserItemViewModel { FileName = "voiceover_take3.wav", DurationDisplay = "00:00:48", Kind = MediaBrowserItemKind.Audio });
        Items.Add(new MediaBrowserItemViewModel { FileName = "logo.png", DurationDisplay = "—", Kind = MediaBrowserItemKind.Image });
        // --------------------------------------------------------------------
    }

    [RelayCommand]
    private void Import() =>
        _logger.LogInformation("Import Media requested (file picker / drag-drop land in Phase 2).");
}
