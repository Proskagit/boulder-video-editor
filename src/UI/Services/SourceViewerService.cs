using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The Source viewer's session state (Phase 16, D031 SQ3 / SQ11 / SQ12): the asset open in Source, its frame grid
/// (<see cref="ITimelineEditService.GetSourceGrid"/>), the last source position, and a source In / Out per asset with the
/// timeline range's rules (<see cref="InOutRange"/>). One instance shared by the Preview (Source mode, transport, the
/// source bar), the timeline (Insert / Overwrite) and the keys. Transient: nothing here touches the project — no
/// <c>project.json</c>, no dirty state, no undo step. Another project (New, Open, Recent, Recover) closes the source and
/// forgets every range; an asset that leaves the project or goes missing is closed and its range dropped; a frame-grid
/// change (the project rate locked or changed) keeps the source times on the new grid.
/// </summary>
public sealed class SourceViewerService
{
    private readonly IProjectService _projects;
    private readonly ITimelineEditService _edit;
    private readonly Dictionary<Guid, InOutRange> _ranges = new();
    private readonly Dictionary<Guid, MediaTime> _positions = new();

    public SourceViewerService(IProjectService projects, ITimelineEditService edit)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _edit = edit ?? throw new ArgumentNullException(nameof(edit));
        projects.ProjectChanged += (_, _) =>
        {
            _ranges.Clear();
            _positions.Clear();
            Close();
        };
        projects.TimelineChanged += (_, _) => RefreshGrid();
        projects.MediaAssetsChanged += (_, _) => OnMediaAssetsChanged();
    }

    /// <summary>The asset open in Source, or null.</summary>
    public MediaAsset? Asset { get; private set; }

    /// <summary>The open asset's frame grid (null when nothing is open).</summary>
    public SourceGrid? Grid { get; private set; }

    /// <summary>The source position last shown (a frame start of <see cref="Grid"/>).</summary>
    public MediaTime Position => Asset is { } a && _positions.TryGetValue(a.Id, out var p) ? p : MediaTime.Zero;

    /// <summary>The open asset's source In / Out.</summary>
    public InOutRange Range => Asset is { } a && _ranges.TryGetValue(a.Id, out var r) ? r : InOutRange.None;

    /// <summary>The source range in whole frames of <see cref="Grid"/>, or null (none set, or nothing open).</summary>
    public (long First, long End)? Frames => Grid is { } g ? Range.Frames(g.Rate, g.Duration) : null;

    /// <summary>What Insert / Overwrite place (D031 §2): the range, or the whole asset without one — as source times on
    /// the grid; null when nothing is open.</summary>
    public (MediaTime In, MediaTime Out)? Selection =>
        Grid is not { } g ? null
        : Frames is { } f ? (MediaTime.FromFrame(f.First, g.Rate), MediaTime.FromFrame(f.End, g.Rate))
        : (MediaTime.Zero, g.Duration);

    /// <summary>The asset, its grid or the position changed (Open, Close, a re-grid).</summary>
    public event EventHandler? SourceChanged;

    /// <summary>The open asset's In / Out changed.</summary>
    public event EventHandler? RangeChanged;

    /// <summary>Opens <paramref name="asset"/> (SQ11). Null when opened, otherwise why not (nothing changes).</summary>
    public string? Open(MediaAsset asset)
    {
        if (!_projects.Current.MediaAssets.Contains(asset)) return "That media is not in the project.";
        if (_edit.GetSourceGrid(asset, out var reason) is not { } grid) return reason;
        Asset = asset;
        Grid = grid;
        if (_ranges.TryGetValue(asset.Id, out var range)) _ranges[asset.Id] = range.Regrid(grid.Rate);
        _positions[asset.Id] = Snap(Position, grid);
        SourceChanged?.Invoke(this, EventArgs.Empty);
        RangeChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public void Close()
    {
        if (Asset is null) return;
        Asset = null;
        Grid = null;
        SourceChanged?.Invoke(this, EventArgs.Empty);
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Playback showed <paramref name="position"/> of the source.</summary>
    public void SetPosition(MediaTime position)
    {
        if (Asset is { } a && Grid is { } g) _positions[a.Id] = Snap(position, g);
    }

    /// <summary><c>I</c> in Source: In at the frame containing <paramref name="position"/>.</summary>
    public void SetIn(MediaTime position) { if (Grid is { } g) SetRange(Range.WithIn(position, g.Rate)); }

    /// <summary><c>O</c> in Source: Out after the frame containing <paramref name="position"/>.</summary>
    public void SetOut(MediaTime position) { if (Grid is { } g) SetRange(Range.WithOut(Min(position, g), g.Rate)); }

    /// <summary>The ✕ on the source bar.</summary>
    public void ClearRange() => SetRange(InOutRange.None);

    private void SetRange(InOutRange range)
    {
        if (Asset is not { } a || range == Range) return;
        _ranges[a.Id] = range;
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A timeline change may lock or change the project rate, and with it the open asset's grid.</summary>
    private void RefreshGrid()
    {
        if (Asset is not { } asset || Grid is not { } old) return;
        if (_edit.GetSourceGrid(asset, out _) is not { } grid) { Close(); return; }
        if (grid == old) return;
        Grid = grid;
        if (_ranges.TryGetValue(asset.Id, out var range)) _ranges[asset.Id] = range.Regrid(grid.Rate);
        _positions[asset.Id] = Snap(Position, grid);
        SourceChanged?.Invoke(this, EventArgs.Empty);
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaAssetsChanged()
    {
        var live = _projects.Current.MediaAssets.ToHashSet();
        foreach (var id in _ranges.Keys.Where(id => live.All(a => a.Id != id)).ToList()) _ranges.Remove(id);
        foreach (var id in _positions.Keys.Where(id => live.All(a => a.Id != id)).ToList()) _positions.Remove(id);
        if (Asset is { } asset && (!live.Contains(asset) || asset.IsMissing)) Close();
        else RefreshGrid();
    }

    /// <summary>The last frame's start at most: a position at the source's end shows its last frame.</summary>
    private static MediaTime Min(MediaTime position, SourceGrid grid) =>
        position.ToFrameFloor(grid.Rate) >= grid.Frames ? MediaTime.FromFrame(grid.Frames - 1, grid.Rate) : position;

    private static MediaTime Snap(MediaTime position, SourceGrid grid) =>
        MediaTime.FromFrame(Math.Clamp(position.ToNearestFrame(grid.Rate), 0, grid.Frames), grid.Rate);
}
