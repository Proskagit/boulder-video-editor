using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry.Research;
using AiVideoEditor.UI.ViewModels.Panels;

namespace AiVideoEditor.UI.Research;

/// <summary>
/// What the reference-marking tool (Step 35.3R1) needs of the Source viewer: the asset shown, its position, opening an
/// asset and going to a position, and — for audit only — the PTS of the frame shown. The ordinary Source viewer does the
/// work; this only reads it and calls what a user can call.
/// </summary>
public interface IStudySourceView
{
    /// <summary>The asset open in Source, or null.</summary>
    MediaAsset? Asset { get; }

    /// <summary>The Source position last shown (source time).</summary>
    MediaTime Position { get; }

    /// <summary>Opens the asset in Source and shows it; null when shown, otherwise why not.</summary>
    string? Open(MediaAsset asset);

    /// <summary>Shows <paramref name="position"/> of the open asset (the Source viewer's own seek).</summary>
    void GoTo(MediaTime position);

    /// <summary>The PTS of the frame shown, only when it is reliable (Source shown, not playing or buffering, current).</summary>
    StudyFramePts? ShownFramePts { get; }

    /// <summary>The asset or the position changed.</summary>
    event EventHandler? Changed;
}

/// <summary>The <see cref="IStudySourceView"/> of the main window's Preview — the existing Source viewer, unchanged.</summary>
public sealed class PreviewSourceView : IStudySourceView
{
    private readonly PreviewViewModel _preview;

    public PreviewSourceView(PreviewViewModel preview)
    {
        _preview = preview;
        if (preview.Source is { } source) source.SourceChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PreviewViewModel.SourceFrame) or nameof(PreviewViewModel.IsSourceMode))
                Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public MediaAsset? Asset => _preview.IsSourceMode ? _preview.Source?.Asset : null;

    public MediaTime Position => _preview.Source?.Position ?? MediaTime.Zero;

    public string? Open(MediaAsset asset) => _preview.OpenSource(asset);

    public void GoTo(MediaTime position)
    {
        if (_preview.Source?.Grid is { } grid) _preview.SeekSource(position.ToNearestFrame(grid.Rate));
    }

    public StudyFramePts? ShownFramePts
    {
        get
        {
            if (!_preview.IsSourceMode || _preview.IsPlaying || _preview.IsBuffering || !_preview.AreLayersCurrent) return null;
            return _preview.Layers.Select(l => l.Frame).FirstOrDefault(f => f is not null) is { } frame
                ? new StudyFramePts(frame.Timestamp.Pts, frame.Timestamp.TimeBase.Numerator, frame.Timestamp.TimeBase.Denominator)
                : null;
        }
    }

    public event EventHandler? Changed;
}
