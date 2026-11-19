using System.Globalization;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// D070 §3: the AI models of the Portable package. At startup — when the application's folder holds the package
/// (<c>ai-models\</c>) and the models folder lacks a pinned file — the user is asked once whether to install them; «Установить»
/// copies them in the background (the status bar shows the progress and a ✕; the editor stays usable), «Не сейчас» leaves the
/// application working without them (each AI feature says its model is missing). «Настройки…» → «ИИ-модели» shows the state and
/// installs (or repairs) again. Nothing is downloaded. The Setup build has no package: its installer has placed the models.
/// </summary>
/// <remarks>Called on the UI thread; the copy runs on the thread pool and reports through <see cref="Progress{T}"/>.</remarks>
public sealed partial class ModelSetupService : ObservableObject
{
    private readonly IModelInstaller _installer;
    private readonly IDialogService? _dialogs;
    private readonly StatusService _status;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;

    /// <param name="packageRoot">The models package next to the application (Portable), or null.</param>
    public ModelSetupService(IModelInstaller installer, string modelsRoot, string? packageRoot, StatusService status,
        IDialogService? dialogs = null, ILogger<ModelSetupService>? logger = null)
    {
        _installer = installer;
        ModelsRoot = modelsRoot;
        PackageRoot = packageRoot;
        _status = status;
        _dialogs = dialogs;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        CancelCommand = new RelayCommand(Cancel, () => IsInstalling);
        InstallCommand = new RelayCommand(() => Start(), () => CanInstall);
    }

    /// <summary>The models folder (<c>%LOCALAPPDATA%\BoulderVideoEditor\models</c>).</summary>
    public string ModelsRoot { get; }

    /// <summary>The models package next to the application (Portable), or null (Setup, a development build).</summary>
    public string? PackageRoot { get; }

    public bool HasPackage => PackageRoot is not null;

    public bool CanInstall => HasPackage && !IsInstalling;

    /// <summary>Starts the installation (the settings window's button).</summary>
    public IRelayCommand InstallCommand { get; }

    /// <summary>Cancels the running installation (the status bar's ✕).</summary>
    public IRelayCommand CancelCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactText))]
    private double _percent;

    /// <summary>The status bar's text while installing.</summary>
    public string CompactText => string.Format(Strings.ModelSetup_CompactText, Percent.ToString("0", CultureInfo.CurrentCulture));

    /// <summary>The last quick check (see <see cref="RefreshAsync"/>); null before the first.</summary>
    [ObservableProperty]
    private ModelInstallState? _state;

    /// <summary>The size of a complete installation, in GB (one decimal).</summary>
    public string TotalGigabytes =>
        (_installer.TotalBytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>Checks the models folder again (sizes only, off the UI thread).</summary>
    public async Task<ModelInstallState> RefreshAsync()
    {
        var state = await Task.Run(() => _installer.Check(ModelsRoot));
        State = state;
        return state;
    }

    /// <summary>After the main window opened (and the recovery question): offers the package's models when some are missing.</summary>
    public async Task OfferAtStartupAsync()
    {
        if (!HasPackage || _dialogs is null || IsInstalling) return;
        ModelInstallState state;
        try
        {
            state = await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking the models folder {Folder} failed.", ModelsRoot);
            return;
        }
        if (state.IsComplete) return;
        _logger.LogInformation("{Count} model file(s) missing in {Folder}; the package {Package} is offered.", state.MissingFiles.Count, ModelsRoot, PackageRoot);

        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = Strings.ModelSetup_OfferTitle,
            Message = string.Format(Strings.ModelSetup_OfferMessage, TotalGigabytes, ModelsRoot),
            Buttons = new[] { Strings.ModelSetup_Install, Strings.ModelSetup_Later }
        });
        if (choice == 0) Start();
        else _logger.LogInformation("The user declined installing the models now.");
    }

    /// <summary>Starts the installation in the background; false when it can't start (no package, already running).</summary>
    public bool Start()
    {
        if (!CanInstall) return false;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Percent = 0;
        IsInstalling = true;
        NotifyCommands();
        _status.Report(Strings.ModelSetup_Started);
        var progress = new Progress<ModelInstallProgress>(p =>
        {
            if (ReferenceEquals(_cancellation, cancellation)) Percent = p.Percent;
        });
        _running = RunAsync(PackageRoot!, progress, cancellation);
        return true;
    }

    /// <summary>The running installation (completed when none runs); never faults.</summary>
    public Task Completion => _running;

    private async Task RunAsync(string package, IProgress<ModelInstallProgress> progress, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            var result = await Task.Run(() => _installer.InstallAsync(package, ModelsRoot, progress, token), token);
            _logger.LogInformation("Models installed from {Package} into {Folder}: copied {Installed}, kept {Kept}.", package, ModelsRoot, result.Installed, result.Kept);
            Percent = 100;
            _status.Report(string.Format(Strings.ModelSetup_Done, result.Installed, result.Kept));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Installing the models was cancelled.");
            _status.ReportWarning(Strings.ModelSetup_Cancelled);
        }
        catch (ModelInstallException ex)
        {
            _logger.LogError(ex, "Installing the models from {Package} failed.", package);
            _status.ReportError(string.Format(Strings.ModelSetup_Failed, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing the models from {Package} failed unexpectedly.", package);
            _status.ReportError(string.Format(Strings.ModelSetup_Failed, ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            IsInstalling = false;
            NotifyCommands();
        }

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking the models folder {Folder} after installing failed.", ModelsRoot);
        }
    }

    private void Cancel()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }
    }

    /// <summary>The main window is about to close: asks whether a running installation may be cancelled. True: nothing runs,
    /// or the user chose «Прервать и закрыть»; false keeps the window open. Nothing is cancelled here — as for the export
    /// (D060 §5), the window cancels it only once every question agreed to close (<see cref="CancelAndWaitAsync"/>).</summary>
    public async Task<bool> AskToCloseAsync()
    {
        if (!IsInstalling) return true;
        if (_dialogs is null) return false;
        var choice = await _dialogs.AskAsync(new DialogRequest
        {
            Title = Strings.ModelSetup_CloseTitle,
            Message = string.Format(Strings.ModelSetup_CloseQuestion, Percent.ToString("0", CultureInfo.CurrentCulture)),
            Buttons = new[] { Strings.ModelSetup_KeepInstalling, Strings.ModelSetup_CancelAndClose }
        });
        return choice == 1;
    }

    /// <summary>Cancels a running installation and waits until it ended (its temporary file removed); never throws.</summary>
    public async Task CancelAndWaitAsync()
    {
        Cancel();
        await _running;
    }

    /// <summary><see cref="AskToCloseAsync"/>, then <see cref="CancelAndWaitAsync"/> when agreed.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (!await AskToCloseAsync()) return false;
        await CancelAndWaitAsync();
        return true;
    }

    private void NotifyCommands()
    {
        CancelCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
}
