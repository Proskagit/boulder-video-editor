using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// Holds the current in-memory <see cref="Core.Entities.Project"/> and mediates every
/// change to it. Phase 2 scope: New Project is real; Open/Save/SaveAs are not
/// implemented yet (they throw <see cref="NotSupportedException"/> with a message
/// clear enough to show the user) — full project.json persistence is Phase 5.
/// </summary>
public sealed class ProjectService : IProjectService
{
    private readonly ILogger<ProjectService> _logger;

    public Core.Entities.Project Current { get; private set; }

    public event EventHandler? ProjectChanged;
    public event EventHandler? MediaAssetsChanged;

    public ProjectService(ILogger<ProjectService> logger)
    {
        _logger = logger;
        Current = new Core.Entities.Project { Name = "Untitled Project" };
    }

    public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null)
    {
        Current = new Core.Entities.Project
        {
            Name = name,
            Settings = settings ?? new ProjectSettings()
        };

        _logger.LogInformation("Created new project '{Name}'.", name);
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        NotifyMediaAssetsChanged();
        return Current;
    }

    public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) =>
        throw new NotSupportedException("Opening saved projects isn't implemented yet — it arrives in Phase 5.");

    public Task SaveAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Saving projects isn't implemented yet — it arrives in Phase 5.");

    public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) =>
        throw new NotSupportedException("Saving projects isn't implemented yet — it arrives in Phase 5.");

    public IReadOnlyList<MediaAsset> DetectMissingMedia()
    {
        var missing = new List<MediaAsset>();
        foreach (var asset in Current.MediaAssets)
        {
            asset.IsMissing = !File.Exists(asset.FilePath);
            if (asset.IsMissing)
                missing.Add(asset);
        }
        return missing;
    }

    public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets)
    {
        var existingPaths = new HashSet<string>(
            Current.MediaAssets.Select(a => a.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var added = new List<MediaAsset>();
        var duplicateCount = 0;

        foreach (var asset in assets)
        {
            if (!existingPaths.Add(asset.FilePath))
            {
                duplicateCount++;
                continue;
            }

            Current.MediaAssets.Add(asset);
            added.Add(asset);
        }

        if (added.Count > 0)
        {
            Current.ModifiedAt = DateTimeOffset.UtcNow;
            Current.IsDirty = true;
            _logger.LogInformation("Added {Count} media asset(s) to the project ({Duplicates} duplicate(s) skipped).", added.Count, duplicateCount);
            NotifyMediaAssetsChanged();
        }

        return new MediaAddResult { Added = added, DuplicateCount = duplicateCount };
    }

    public void NotifyMediaAssetsChanged() => MediaAssetsChanged?.Invoke(this, EventArgs.Empty);
}
