using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Services;

namespace AiVideoEditor.UI.Tests;

/// <summary>Answers dialogs from a queue (null = closed without choosing) and records what was asked.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    private readonly Queue<int?> _answers;

    public ScriptedDialogs(params int?[] answers) => _answers = new Queue<int?>(answers);

    public List<DialogRequest> Asked { get; } = new();

    /// <summary>Runs when a dialog is shown, before it is answered.</summary>
    public Action? OnAsk { get; init; }

    public Task<int?> AskAsync(DialogRequest request)
    {
        Asked.Add(request);
        OnAsk?.Invoke();
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}

/// <summary>Autosave that does nothing (for tests not about autosave).</summary>
internal sealed class NullAutosave : IAutosaveService
{
    public event EventHandler<string>? AutosaveCompleted { add { } remove { } }
    public void Start() { }
    public void Stop() { }
    public Task<bool> AutosaveNowAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<RecoveryScanResult> FindRecoveryAsync(CancellationToken ct = default) => Task.FromResult(RecoveryScanResult.None);
    public Task DiscardRecoveryAsync(RecoveryCandidate candidate) => Task.CompletedTask;
    public Task DiscardRecoveryAsync(Guid projectId) => Task.CompletedTask;
    public Task ShutdownAsync(bool keepUnsavedChanges = true) => Task.CompletedTask;
}

/// <summary>Answers folder pickers from a queue (null = cancelled) and records the titles.</summary>
internal sealed class ScriptedPicker : IFilePickerService
{
    private readonly Queue<string?> _folders;

    public ScriptedPicker(params string?[] folders) => _folders = new Queue<string?>(folders);

    public List<string> Titles { get; } = new();

    public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerRequest request, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
    {
        Titles.Add(request.Title);
        return Task.FromResult(_folders.Count > 0 ? _folders.Dequeue() : null);
    }
}
