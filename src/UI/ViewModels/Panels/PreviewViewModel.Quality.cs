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

    /// <summary>Amendment 2: the bar's compact button shows the chosen quality short («1080p»).</summary>
    public string QualityShort => Quality switch
    {
        PreviewQuality.Original => Strings.PreviewQuality_ShortOriginal,
        PreviewQuality.High => Strings.PreviewQuality_ShortHigh,
        PreviewQuality.Low => Strings.PreviewQuality_ShortLow,
        _ => Strings.PreviewQuality_ShortMedium
    };

    /// <summary>The button's menu: a quality by its index in <see cref="QualityNames"/>.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetQuality(string index)
    {
        if (int.TryParse(index, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)) QualityIndex = i;
    }

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
                OnPropertyChanged(nameof(QualityShort));
                if (!IsSourceMode && !_released) RebuildSnapshot();
                return;
            }
            _settings.Update(s => s with { PreviewQuality = quality });   // Changed rebuilds the snapshot
            OnPropertyChanged();
            OnPropertyChanged(nameof(QualityShort));
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
            // Amendment 2: the one 1080p proxy serves «Среднее» and «Минимум» (scaled on the fly); the others decode the source.
            var proxy = media is not null && PreviewDecoding.UsesProxy(quality) && PreviewDecoding.NeedsProxy(media) ? _proxies?.ProxyFor(asset.Id) : null;
            return asset with { DecodeBound = bound, DecodePath = proxy };
        });
    }
}
