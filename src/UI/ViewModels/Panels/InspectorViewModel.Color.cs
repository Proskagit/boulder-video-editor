using System.Runtime.InteropServices;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- «Цвет» (D069 §7): a video clip's colour correction ---------------------------------------------------------------------
// Eight sliders −100…+100, each change one Undo step — the consecutive changes of one slider (a drag, the arrows) merge into
// one (the same rule as the Inspector's other fields); the section's reset and «Автоулучшение» one step each.
public sealed partial class InspectorViewModel
{
    [ObservableProperty] private decimal? _colorExposure;
    [ObservableProperty] private decimal? _colorBrightness;
    [ObservableProperty] private decimal? _colorContrast;
    [ObservableProperty] private decimal? _colorHighlights;
    [ObservableProperty] private decimal? _colorShadows;
    [ObservableProperty] private decimal? _colorSaturation;
    [ObservableProperty] private decimal? _colorTemperature;
    [ObservableProperty] private decimal? _colorTint;

    /// <summary>True while «Автоулучшение» analyses the clip's frames.</summary>
    [ObservableProperty] private bool _isEnhancing;

    public decimal MinColor => ColorCorrection.Min;
    public decimal MaxColor => ColorCorrection.Max;

    /// <summary>The section is shown for a video clip (a picture of its own).</summary>
    public bool HasColor => _clip is VideoClip;

    /// <summary>The frames «Автоулучшение» measures (null: the button does nothing — a test without a decoder).</summary>
    public IClipFrameSampler? ColorFrames { get; set; }

    private MediaAsset? _clipAsset;
    private FrameRate _clipRate = FrameRate.Fps30;

    /// <summary>The slider the last change came from and its gesture: the next change of the same slider merges into it.</summary>
    private (Guid Clip, int Index, object Gesture)? _colorGesture;

    partial void OnColorExposureChanged(decimal? value) => EditColor(0, value);
    partial void OnColorBrightnessChanged(decimal? value) => EditColor(1, value);
    partial void OnColorContrastChanged(decimal? value) => EditColor(2, value);
    partial void OnColorHighlightsChanged(decimal? value) => EditColor(3, value);
    partial void OnColorShadowsChanged(decimal? value) => EditColor(4, value);
    partial void OnColorSaturationChanged(decimal? value) => EditColor(5, value);
    partial void OnColorTemperatureChanged(decimal? value) => EditColor(6, value);
    partial void OnColorTintChanged(decimal? value) => EditColor(7, value);

    private void EditColor(int index, decimal? value)
    {
        if (_syncing || value is not { } v || _clip is not VideoClip clip || RejectWhileLocked()) return;
        var whole = (int)Math.Round(Math.Clamp(v, MinColor, MaxColor), MidpointRounding.AwayFromZero);
        if (_colorGesture is not { } g || g.Clip != clip.Id || g.Index != index)
            _colorGesture = (clip.Id, index, new object());
        var result = _edit.SetClipColor(clip.Id, clip.Color.With(index, whole), _colorGesture!.Value.Gesture);
        if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
        if (!result.Success || whole != v) SyncColor();   // the field shows what the model has
    }

    /// <summary>Fills the eight fields from the shown clip (no edit).</summary>
    private void SyncColor()
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            var c = (_clip as VideoClip)?.Color ?? ColorCorrection.Neutral;
            ColorExposure = c.Exposure;
            ColorBrightness = c.Brightness;
            ColorContrast = c.Contrast;
            ColorHighlights = c.Highlights;
            ColorShadows = c.Shadows;
            ColorSaturation = c.Saturation;
            ColorTemperature = c.Temperature;
            ColorTint = c.Tint;
        }
        finally
        {
            _syncing = was;
        }
        OnPropertyChanged(nameof(HasColor));
    }

    /// <summary>«Цвет» back to neutral — one Undo step.</summary>
    [RelayCommand]
    private void ResetColor()
    {
        if (_clip is not VideoClip clip || RejectWhileLocked()) return;
        _colorGesture = null;
        var result = _edit.SetClipColor(clip.Id, ColorCorrection.Neutral);
        if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
    }

    /// <summary>«Автоулучшение»: measures eight small frames of the clip and sets the sliders it calls for — one Undo step,
    /// adjustable by hand afterwards.</summary>
    [RelayCommand]
    private async Task AutoEnhance()
    {
        if (_clip is not VideoClip clip || IsEnhancing || RejectWhileLocked()) return;
        if (ColorFrames is null || _clipAsset is not { IsMissing: false } asset)
        {
            _status.ReportWarning(Strings.Color_AutoUnavailable);
            return;
        }
        IsEnhancing = true;
        _status.Report(Strings.Color_AutoRunning);
        try
        {
            var frames = await ColorFrames.SampleAsync(clip, asset, _clipRate, 8, 320, CancellationToken.None);
            if (frames.Count == 0)
            {
                _status.ReportWarning(Strings.Color_AutoNoFrames);
                return;
            }
            var stats = Core.Color.AutoEnhance.Measure(frames.Select(f =>
                (MemoryMarshal.TryGetArray(f.Pixels, out var a) ? a.Array! : f.Pixels.ToArray(), f.Width, f.Height, f.Stride)));
            var suggested = Core.Color.AutoEnhance.Suggest(stats);
            if (_clip != clip || RejectWhileLocked()) return;   // another clip shown or an export started meanwhile
            _colorGesture = null;
            var result = _edit.SetClipColor(clip.Id, suggested);
            if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
            else _status.Report(Strings.Color_AutoDone);
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
