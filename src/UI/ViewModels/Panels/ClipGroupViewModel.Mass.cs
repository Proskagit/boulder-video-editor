using System.Runtime.InteropServices;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- D069 amendment 1: the group's mass settings (the owner's check) --------------------------------------------------------
// The clean-up (noise reduction, voice enhancement) of every clip with sound, the fades of every clip, the colour of every video
// clip with «Автоулучшение» — each change one Undo step over the group, a field empty («—») where the clips differ.
public sealed partial class ClipGroupViewModel
{
    [ObservableProperty] private decimal? _noiseReduction;
    [ObservableProperty] private decimal? _voiceEnhancement;
    [ObservableProperty] private decimal? _fadeInFrames;
    [ObservableProperty] private decimal? _fadeOutFrames;
    [ObservableProperty] private bool _hasFades;
    [ObservableProperty] private bool _hasColor;
    [ObservableProperty] private bool _isEnhancing;

    [ObservableProperty] private decimal? _colorExposure;
    [ObservableProperty] private decimal? _colorBrightness;
    [ObservableProperty] private decimal? _colorContrast;
    [ObservableProperty] private decimal? _colorHighlights;
    [ObservableProperty] private decimal? _colorShadows;
    [ObservableProperty] private decimal? _colorSaturation;
    [ObservableProperty] private decimal? _colorTemperature;
    [ObservableProperty] private decimal? _colorTint;

    /// <summary>The project's frame rate (the fades are whole frames of it).</summary>
    public FrameRate Rate { get; set; } = FrameRate.Fps30;

    /// <summary>The frames «Автоулучшение» measures (null: the button only says it cannot).</summary>
    public IClipFrameSampler? ColorFrames { get; set; }

    public decimal MaxCleanup => AudioCleanup.MaxStrength;
    public decimal MinColor => ColorCorrection.Min;
    public decimal MaxColor => ColorCorrection.Max;

    private IReadOnlyList<Clip> _faded = Array.Empty<Clip>();

    private void ShowMass()
    {
        _faded = _items.SelectMany(i => i).ToList();
        HasFades = _faded.Count > 0;
        HasColor = _videos.Count > 0;
    }

    private void LoadMass()
    {
        NoiseReduction = Same(_sounds.Select(c => (decimal)(AudioProperties.Of(c)!.Value.Cleanup?.NoiseReduction ?? 0)));
        VoiceEnhancement = Same(_sounds.Select(c => (decimal)(AudioProperties.Of(c)!.Value.Cleanup?.VoiceEnhancement ?? 0)));
        FadeInFrames = Same(_faded.Select(c => (decimal)c.FadeIn.ToNearestFrame(Rate)));
        FadeOutFrames = Same(_faded.Select(c => (decimal)c.FadeOut.ToNearestFrame(Rate)));
        for (var i = 0; i < 8; i++)
        {
            var index = i;
            SetColorField(i, Same(_videos.Select(v => (decimal)v.Color.Values[index])));
        }
    }

    private void SetColorField(int index, decimal? value)
    {
        switch (index)
        {
            case 0: ColorExposure = value; break;
            case 1: ColorBrightness = value; break;
            case 2: ColorContrast = value; break;
            case 3: ColorHighlights = value; break;
            case 4: ColorShadows = value; break;
            case 5: ColorSaturation = value; break;
            case 6: ColorTemperature = value; break;
            default: ColorTint = value; break;
        }
    }

    partial void OnNoiseReductionChanged(decimal? value) => EditCleanup(value, noise: true);
    partial void OnVoiceEnhancementChanged(decimal? value) => EditCleanup(value, noise: false);

    private void EditCleanup(decimal? value, bool noise)
    {
        if (_syncing || value is not { } strength) return;
        var whole = (int)Math.Clamp(Math.Round(strength, MidpointRounding.AwayFromZero), 0, AudioCleanup.MaxStrength);
        Apply(noise ? "Change Noise Reduction (Group)" : "Change Voice Enhancement (Group)", Key(noise ? "noise" : "voice", _sounds),
            () => _sounds.Select(c =>
            {
                var audio = AudioProperties.Of(c)!.Value;
                var current = audio.Cleanup ?? AudioCleanup.None;
                var next = noise ? current with { NoiseReduction = whole } : current with { VoiceEnhancement = whole };
                return _edit.SetClipProperties(c.Id, new ClipPropertyChange { Audio = audio with { Cleanup = AudioCleanup.Normalize(next) } });
            }));
    }

    partial void OnFadeInFramesChanged(decimal? value) => EditFade(value, fadeIn: true);
    partial void OnFadeOutFramesChanged(decimal? value) => EditFade(value, fadeIn: false);

    /// <summary>Each clip's fade, at most half of the clip (the single clip's rule: a fade never longer than its clip, D025).</summary>
    private void EditFade(decimal? value, bool fadeIn)
    {
        if (_syncing || value is not { } frames) return;
        var whole = (long)Math.Max(0, Math.Round(frames, MidpointRounding.AwayFromZero));
        Apply(fadeIn ? "Change Fade In (Group)" : "Change Fade Out (Group)", Key(fadeIn ? "fadein" : "fadeout", _faded), () => _faded.Select(c =>
        {
            var fade = FadeProperties.Of(c);
            var length = MediaTime.FromFrame(Math.Min(whole, c.Duration.ToNearestFrame(Rate)), Rate);
            return _edit.SetClipProperties(c.Id, new ClipPropertyChange { Fade = fadeIn ? fade with { FadeIn = length } : fade with { FadeOut = length } });
        }));
    }

    partial void OnColorExposureChanged(decimal? value) => EditColor(0, value);
    partial void OnColorBrightnessChanged(decimal? value) => EditColor(1, value);
    partial void OnColorContrastChanged(decimal? value) => EditColor(2, value);
    partial void OnColorHighlightsChanged(decimal? value) => EditColor(3, value);
    partial void OnColorShadowsChanged(decimal? value) => EditColor(4, value);
    partial void OnColorSaturationChanged(decimal? value) => EditColor(5, value);
    partial void OnColorTemperatureChanged(decimal? value) => EditColor(6, value);
    partial void OnColorTintChanged(decimal? value) => EditColor(7, value);

    /// <summary>One slider over every video clip of the group: that value set, each clip's other values kept.</summary>
    private void EditColor(int index, decimal? value)
    {
        if (_syncing || value is not { } v) return;
        var whole = (int)Math.Round(Math.Clamp(v, ColorCorrection.Min, ColorCorrection.Max), MidpointRounding.AwayFromZero);
        Apply("Change Colour (Group)", Key("color" + index, _videos),
            () => _videos.Select(c => _edit.SetClipColor(c.Id, c.Color.With(index, whole))));
    }

    [RelayCommand]
    private void ResetColor() =>
        Apply("Reset Colour (Group)", null, () => _videos.Select(c => _edit.SetClipColor(c.Id, ColorCorrection.Neutral)));

    /// <summary>«Автоулучшение» of the group: each video clip by its own frames, all in one Undo step.</summary>
    [RelayCommand]
    private async Task AutoEnhance()
    {
        if (IsEnhancing || _videos.Count == 0) return;
        if (ColorFrames is null)
        {
            _status.ReportWarning(Strings.Color_AutoUnavailable);
            return;
        }
        IsEnhancing = true;
        _status.Report(Strings.Color_AutoRunning);
        try
        {
            var videos = _videos.ToList();
            var looks = new Dictionary<Guid, ColorCorrection>();
            foreach (var video in videos)
            {
                if (_asset(video.MediaAssetId) is not { IsMissing: false } asset) continue;
                var frames = await ColorFrames.SampleAsync(video, asset, Rate, 8, 320, CancellationToken.None);
                if (frames.Count == 0) continue;
                looks[video.Id] = Core.Color.AutoEnhance.Suggest(Core.Color.AutoEnhance.Measure(frames.Select(f =>
                    (MemoryMarshal.TryGetArray(f.Pixels, out var a) ? a.Array! : f.Pixels.ToArray(), f.Width, f.Height, f.Stride))));
            }
            if (looks.Count == 0)
            {
                _status.ReportWarning(Strings.Color_AutoNoFrames);
                return;
            }
            Apply("Auto Enhance (Group)", null, () => videos.Where(v => looks.ContainsKey(v.Id)).Select(v => _edit.SetClipColor(v.Id, looks[v.Id])));
            _status.Report(Strings.Color_AutoDone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.ReportError(string.Format(Strings.Color_AutoFailed, ex.Message));
        }
        finally
        {
            IsEnhancing = false;
        }
    }
}
