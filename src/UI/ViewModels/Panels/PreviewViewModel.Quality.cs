using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>D071 §2, §4: the Preview's picture quality on its bar and the decoding it gives the snapshot — the bound of every
/// source and, for a 4K / HEVC video with a ready proxy, the proxy instead of the source. The export never takes it.</summary>
public sealed partial class PreviewViewModel
{
    private ProxyCoordinator? _proxies;

    /// <summary>The bar's choices, in the order of <see cref="PreviewQuality"/>.</summary>
    public IReadOnlyList<string> QualityNames { get; } = new[]
    {
        Strings.PreviewQuality_Original, Strings.PreviewQuality_High, Strings.PreviewQuality_Medium, Strings.PreviewQuality_Low
    };

    /// <summary>The chosen quality's index in <see cref="QualityNames"/>; saved in the user's settings at once.</summary>
    public int QualityIndex
    {
        get => (int)(_settings?.Current.PreviewQuality ?? _sessionQuality);
        set
        {
            if (value < 0 || value >= QualityNames.Count || value == QualityIndex) return;
            var quality = (PreviewQuality)value;
            if (_settings is null)
            {
                _sessionQuality = quality;   // without the settings (tests): this session only
                OnPropertyChanged();
                if (!IsSourceMode && !_released) RebuildSnapshot();
                return;
            }
            _settings.Update(s => s with { PreviewQuality = quality });   // Changed rebuilds the snapshot
            OnPropertyChanged();
        }
    }

    private PreviewQuality _sessionQuality = PreviewDecoding.DefaultQuality;

    private PreviewQuality Quality => (PreviewQuality)QualityIndex;

    private void InitQuality(ProxyCoordinator? proxies)
    {
        _proxies = proxies;
        if (_proxies is not null) _proxies.ProxyReady += (_, _) =>
        {
            if (!IsSourceMode && !_released) RebuildSnapshot();   // the proxy replaces the source at once
        };
    }

    /// <summary>The snapshot's assets decoded at the chosen quality (D071 §4).</summary>
    private PlaybackSnapshot WithPreviewDecoding(PlaybackSnapshot snapshot)
    {
        var quality = Quality;
        var assets = _projectService.Current.MediaAssets.ToDictionary(a => a.Id);
        return snapshot.WithAssets(asset =>
        {
            if (asset.Kind != MediaKind.Video) return asset;
            assets.TryGetValue(asset.Id, out var media);
            if (media is null && _source?.Asset is { } shown && shown.Id == asset.Id) media = shown;
            var m = media?.Metadata;
            var (w, h) = (m?.DisplayWidth ?? m?.Width, m?.DisplayHeight ?? m?.Height);
            var bound = PreviewDecoding.Bound(quality, w, h) ?? (w is > 0 && h is > 0 ? new FrameSize(w.Value, h.Value) : (FrameSize?)null);
            var proxy = media is not null && PreviewDecoding.NeedsProxy(media, quality) ? _proxies?.ProxyFor(asset.Id, quality) : null;
            return asset with { DecodeBound = bound, DecodePath = proxy };
        });
    }
}
