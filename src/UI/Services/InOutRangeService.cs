using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The timeline's In / Out range for this session (Phase 15 Step 15.7, D030 §8): one instance shared by the timeline
/// (I, O, the range bar and its ✕), the Preview (the loop over the range) and the export (Range / Entire sequence). The
/// rules are <see cref="InOutRange"/>'s (Core). Transient session state: nothing here touches the project — no
/// <c>project.json</c>, no recovery file, no dirty state, no undo step. Another project (New, Open, Recent, Recover)
/// starts without a range; a frame-rate change keeps the points' times on the new grid.
/// </summary>
public sealed class InOutRangeService
{
    private readonly IProjectService _projects;
    private FrameRate _rate;

    public InOutRangeService(IProjectService projects)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _rate = Rate;
        projects.ProjectChanged += (_, _) =>
        {
            _rate = Rate;
            Set(InOutRange.None);
        };
        projects.TimelineChanged += (_, _) =>
        {
            if (Rate == _rate) return;
            _rate = Rate;
            Set(Range.Regrid(_rate));
        };
    }

    public InOutRange Range { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Range"/> changes.</summary>
    public event EventHandler? RangeChanged;

    private FrameRate Rate => _projects.Current.Settings.FrameRate;

    /// <summary>The range in whole timeline frames within the current sequence, or null (none set, or nothing of the
    /// sequence inside it).</summary>
    public (long First, long End)? Frames => Range.Frames(Rate, _projects.Current.Timeline.Duration());

    /// <summary><c>I</c>: In at the frame containing <paramref name="playhead"/> (Q7 / Q8).</summary>
    public void SetIn(MediaTime playhead) => Set(Range.WithIn(playhead, Rate));

    /// <summary><c>O</c>: Out after the frame containing <paramref name="playhead"/> (Q7 / Q8).</summary>
    public void SetOut(MediaTime playhead) => Set(Range.WithOut(playhead, Rate));

    /// <summary>The ✕ on the range bar (Q11).</summary>
    public void Clear() => Set(InOutRange.None);

    private void Set(InOutRange range)
    {
        if (range == Range) return;
        Range = range;
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }
}
