using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// The UI side of relinking missing media (D026 §3–§5, Phase 11 Step 11.6), in the style of
/// <see cref="MediaImportWorkflow"/> / <see cref="ExportWorkflow"/>: pickers, questions and messages around the relink
/// service — no relink rule of its own. Every check, the probe, the warnings, the undoable step and the batch matching
/// are <see cref="IMediaRelinkService"/>'s; what the editor shows afterwards follows the project's events.
/// <list type="bullet">
/// <item>Relink: a single-file picker (the asset's kind, starting in its old folder when that is there) →
/// <see cref="IMediaRelinkService.CheckAsync"/> → a rejection is explained and nothing is applied; warnings are
/// confirmed or cancelled → <see cref="IMediaRelinkService.ApplyAsync"/>. Only after a relink that was applied, and only
/// while other media are offline, the user is offered to look for them in the chosen file's folder (D026 §4).</item>
/// <item>Find Missing: a folder picker → <see cref="IMediaRelinkService.SearchFolderAsync"/> → its summary to confirm →
/// <see cref="IMediaRelinkService.ApplyAllAsync"/> (one undoable step); items refused at Apply are listed.</item>
/// <item>The <see cref="EditingLock"/> (an export) is respected: the commands are disabled while it is held, and since an
/// export may start while a check or search runs, it is looked at again after every await and right before applying —
/// nothing is applied while it is held. One workflow at a time (<see cref="IsRunning"/>).</item>
/// </list>
/// </summary>
public sealed class MediaRelinkWorkflow
{
    private readonly IMediaRelinkService _relink;
    private readonly IProjectService _projects;
    private readonly IFilePickerService _picker;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly EditingLock _editingLock;
    private readonly ILogger<MediaRelinkWorkflow> _logger;
    private readonly Func<string, bool> _folderExists;
    private bool _running;

    public MediaRelinkWorkflow(IMediaRelinkService relink, IProjectService projects, IFilePickerService picker,
        IDialogService dialogs, StatusService status, EditingLock editingLock, ILogger<MediaRelinkWorkflow> logger)
        : this(relink, projects, picker, dialogs, status, editingLock, logger, Directory.Exists)
    {
    }

    /// <param name="folderExists">Whether a start folder for a picker is there (run off the UI thread).</param>
    internal MediaRelinkWorkflow(IMediaRelinkService relink, IProjectService projects, IFilePickerService picker,
        IDialogService dialogs, StatusService status, EditingLock editingLock, ILogger<MediaRelinkWorkflow> logger,
        Func<string, bool> folderExists)
    {
        _relink = relink;
        _projects = projects;
        _picker = picker;
        _dialogs = dialogs;
        _status = status;
        _editingLock = editingLock;
        _logger = logger;
        _folderExists = folderExists;
    }

    /// <summary>True from the start of a relink or a search until it has ended (its dialogs included).</summary>
    public bool IsRunning => _running;

    public event EventHandler? IsRunningChanged;

    private void SetRunning(bool value)
    {
        _running = value;
        IsRunningChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> RunAsync(Func<Task<bool>> work)
    {
        if (_running || _editingLock.IsLocked) return false;
        SetRunning(true);
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Relinking failed unexpectedly.");
            _status.Report(Strings.Relink_Unexpected);
            return false;
        }
        finally
        {
            SetRunning(false);
        }
    }

    /// <summary>An export holds the lock now: said, and the caller stops before applying anything.</summary>
    private bool ExportRunning()
    {
        if (!_editingLock.IsLocked) return false;
        _status.Report(Strings.Relink_ExportRunning);
        return true;
    }

    private Task Tell(string title, string message) =>
        _dialogs.AskAsync(new DialogRequest { Title = title, Message = message, Buttons = new[] { Strings.Common_OK } });

    private async Task<string?> ExistingFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            return await Task.Run(() => _folderExists(folder)) ? folder : null;   // a gone drive may take seconds
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FolderOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    // ---- Relink one asset --------------------------------------------------------------------------------------------

    /// <summary>Relinks the offline asset to a file the user picks. Returns true when the relink was applied.</summary>
    public Task<bool> RelinkAsync(Guid assetId) => RunAsync(() => RelinkCoreAsync(assetId));

    private async Task<bool> RelinkCoreAsync(Guid assetId)
    {
        if (_projects.Current.MediaAssets.FirstOrDefault(a => a.Id == assetId) is not { } asset) return false;
        if (!asset.IsMissing)
        {
            _status.Report(string.Format(Strings.Relink_NotOffline, asset.FileName));
            return false;
        }

        var extensions = MediaFileTypes.ByExtension.Where(p => p.Value == asset.Kind).Select(p => "*" + p.Key).ToList();
        var files = await _picker.PickFilesAsync(new FilePickerRequest
        {
            Title = string.Format(Strings.Relink_PickerTitle, asset.FileName),
            AllowMultiple = false,
            StartFolder = await ExistingFolderAsync(FolderOf(asset.FilePath)),
            FileTypeFilters = new[] { new FilePickerFileTypeFilter { Name = MediaKindText.FileFilter(asset.Kind), Patterns = extensions } }
        });
        if (files.Count == 0) return false;                          // the picker was cancelled
        if (ExportRunning()) return false;

        var check = await _relink.CheckAsync(assetId, files[0]);
        if (ExportRunning()) return false;
        if (!check.CanApply)
        {
            _status.Report(string.Format(Strings.Relink_NotRelinked, asset.FileName));
            await Tell(Strings.Relink_CantTitle, check.Message);
            return false;
        }

        if (check.Warnings.Count > 0)
        {
            // Without ffprobe nothing was compared: say that it was not checked, never that the file differs (D4).
            var notChecked = check.Warnings.Any(w => w.Kind == RelinkWarningKind.NotChecked);
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = notChecked ? Strings.Relink_UncheckedTitle : Strings.Relink_DifferencesTitle,
                Message = string.Format(notChecked ? Strings.Relink_Unchecked : Strings.Relink_Differs,
                              Path.GetFileName(check.FilePath), asset.FileName) + "\n\n" +
                          string.Join("\n", check.Warnings.Select(w => "• " + w.Message)) +
                          "\n\n" + Strings.Relink_ClipsStay,
                Buttons = new[] { Strings.Relink_Anyway, Strings.Common_Cancel }
            });
            if (choice != 0)
            {
                _status.Report(Strings.Relink_Cancelled);
                return false;
            }
        }

        if (ExportRunning()) return false;                           // right before applying
        var result = await _relink.ApplyAsync(check);
        if (!result.Applied)
        {
            _status.Report(string.Format(Strings.Relink_NotRelinked, asset.FileName));
            await Tell(Strings.Relink_NotAppliedTitle, result.Message);
            return false;
        }
        _status.Report(result.Message);

        // Only after a relink that was applied, and only while others are offline (D026 §4, product owner, Step 11.6).
        var others = _projects.Current.MediaAssets.Count(a => a.IsMissing);
        if (others > 0 && FolderOf(check.FilePath) is { } folder)
        {
            var search = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Relink_FindOthersTitle,
                Message = Plural.Format(others, Strings.Relink_FindOthers, folder),
                Buttons = new[] { Strings.Relink_Search, Strings.Relink_NotNow }
            });
            if (search == 0)
                await SearchAndApplyAsync(folder);
        }
        return true;
    }

    // ---- Find missing media in a folder -------------------------------------------------------------------------------

    /// <summary>Looks for the offline media in a folder the user picks and relinks what the user confirms. Returns true
    /// when anything was relinked.</summary>
    public Task<bool> FindMissingAsync() => RunAsync(FindMissingCoreAsync);

    private async Task<bool> FindMissingCoreAsync()
    {
        var offline = _projects.Current.MediaAssets.FirstOrDefault(a => a.IsMissing);
        if (offline is null)
        {
            _status.Report(Strings.Relink_NoneOffline);
            return false;
        }

        var folder = await _picker.PickFolderAsync(new FolderPickerRequest
        {
            Title = Strings.Relink_FolderPickerTitle,
            StartFolder = await ExistingFolderAsync(FolderOf(offline.FilePath))
        });
        if (folder is null) return false;
        return await SearchAndApplyAsync(folder);
    }

    private async Task<bool> SearchAndApplyAsync(string folder)
    {
        if (ExportRunning()) return false;
        var search = await _relink.SearchFolderAsync(folder);
        if (ExportRunning()) return false;

        var applicable = search.Applicable;
        if (search.Problem is not null || applicable.Count == 0)
        {
            // Matches that can't be used are not "nothing found" (D5).
            var unusable = search.Entries.Any(e => e.Outcome is RelinkSearchOutcome.Rejected or RelinkSearchOutcome.Ambiguous);
            _status.Report(search.Problem ?? (unusable
                ? Strings.Relink_FoundUnusable
                : Strings.Relink_NothingFound));
            await Tell(Strings.Relink_FindTitle, search.Summary());
            return false;
        }

        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = Strings.Relink_FindTitle,
            Message = search.Summary(),
            Buttons = new[] { Plural.Format(applicable.Count, Strings.Relink_RelinkFiles), Strings.Common_Cancel }
        });
        if (choice != 0)
        {
            _status.Report(Strings.Relink_NothingRelinked);
            return false;
        }

        if (ExportRunning()) return false;                           // right before applying
        var result = await _relink.ApplyAllAsync(applicable);
        var refused = result.Results.Where(r => !r.Applied).ToList();
        _status.Report(Plural.Format(result.AppliedCount, Strings.Relink_Relinked));
        if (refused.Count > 0)
        {
            string Name(Guid id) => _projects.Current.MediaAssets.FirstOrDefault(a => a.Id == id)?.FileName ?? Strings.Relink_Removed;
            await Tell(string.Format(Strings.Relink_PartialTitle, result.AppliedCount, result.Results.Count),
                Strings.Relink_Refused + "\n\n" + string.Join("\n", refused.Select(r => $"• {Name(r.AssetId)}: {r.Message}")));
        }
        return result.AppliedCount > 0;
    }
}
