using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D070 §3: the Portable package's models — offered at the start only when the package is there and a model file is missing;
/// «Не сейчас» leaves the application without them; «Установить» runs in the background (the status bar's progress, ✕), a
/// failure or a cancel is reported and the installation can be started again; closing while it runs asks first.
/// </summary>
public sealed class ModelSetupServiceTests
{
    private const string Models = @"C:\Users\u\AppData\Local\BoulderVideoEditor\models";
    private const string Package = @"D:\Portable\BoulderVideoEditor\ai-models";

    private sealed class FakeInstaller : IModelInstaller
    {
        public ModelInstallState State { get; set; } = new(new[] { @"sam2.1-large\sam2.1_large.encoder.onnx" });
        public int Installs { get; private set; }
        public TaskCompletionSource<ModelInstallResult> Result { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken { get; private set; }
        public IProgress<ModelInstallProgress>? LastProgress { get; private set; }

        public long TotalBytes => 1_200_000_000;

        public ModelInstallState Check(string modelsRoot) => State;

        public async Task<ModelInstallResult> InstallAsync(string packageRoot, string modelsRoot, IProgress<ModelInstallProgress>? progress,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Package, packageRoot);
            Assert.Equal(Models, modelsRoot);
            Installs++;
            LastToken = cancellationToken;
            LastProgress = progress;
            await using var _ = cancellationToken.Register(() => Result.TrySetCanceled(cancellationToken));
            return await Result.Task;
        }

        public void Next() => Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Without_the_package_nothing_is_offered_and_nothing_can_be_installed()
    {
        var installer = new FakeInstaller();
        var dialogs = new ScriptedDialogs(0);
        var setup = new ModelSetupService(installer, Models, packageRoot: null, new StatusService(), dialogs);

        await setup.OfferAtStartupAsync();

        Assert.Empty(dialogs.Asked);
        Assert.False(setup.CanInstall);
        Assert.False(setup.Start());
        Assert.Equal(0, installer.Installs);
    }

    [Fact]
    public async Task With_every_model_in_place_nothing_is_offered()
    {
        var installer = new FakeInstaller { State = new ModelInstallState(Array.Empty<string>()) };
        var dialogs = new ScriptedDialogs(0);
        var setup = new ModelSetupService(installer, Models, Package, new StatusService(), dialogs);

        await setup.OfferAtStartupAsync();

        Assert.Empty(dialogs.Asked);
        Assert.True(setup.State!.IsComplete);
    }

    [Fact]
    public async Task Declining_leaves_the_application_without_the_models()
    {
        var installer = new FakeInstaller();
        var dialogs = new ScriptedDialogs(1);                               // «Не сейчас»
        var setup = new ModelSetupService(installer, Models, Package, new StatusService(), dialogs);

        await setup.OfferAtStartupAsync();

        var asked = Assert.Single(dialogs.Asked);
        Assert.Equal(new[] { Strings.ModelSetup_Install, Strings.ModelSetup_Later }, asked.Buttons);
        Assert.Contains(Models, asked.Message);
        Assert.Equal(0, installer.Installs);
        Assert.False(setup.IsInstalling);
        Assert.True(setup.CanInstall);                                       // still possible later from the settings
    }

    [Fact]
    public async Task Accepting_installs_in_the_background_with_progress_and_reports_the_result()
    {
        var installer = new FakeInstaller();
        var status = new StatusService();
        var setup = new ModelSetupService(installer, Models, Package, status, new ScriptedDialogs(0));

        await setup.OfferAtStartupAsync();                                   // returns while the copy runs

        Assert.True(setup.IsInstalling);
        Assert.False(setup.CanInstall);
        Assert.True(setup.CancelCommand.CanExecute(null));
        await Until(() => installer.Installs == 1);
        installer.LastProgress!.Report(new ModelInstallProgress(600, 1_200, "x"));
        await Until(() => setup.Percent == 50);
        Assert.Equal(string.Format(Strings.ModelSetup_CompactText, "50"), setup.CompactText);

        installer.State = new ModelInstallState(Array.Empty<string>());
        installer.Result.SetResult(new ModelInstallResult(11, 2));
        await setup.Completion;

        Assert.False(setup.IsInstalling);
        Assert.Equal(StatusKind.Info, status.Kind);
        Assert.Equal(string.Format(Strings.ModelSetup_Done, 11, 2), status.Message);
        await Until(() => setup.State?.IsComplete == true);
    }

    [Fact]
    public async Task A_failure_is_reported_and_the_installation_can_be_started_again()
    {
        var installer = new FakeInstaller();
        var status = new StatusService();
        var setup = new ModelSetupService(installer, Models, Package, status, new ScriptedDialogs());

        Assert.True(setup.Start());
        await Until(() => installer.Installs == 1);
        installer.Result.SetException(new ModelInstallException("disk full."));
        await setup.Completion;

        Assert.Equal(StatusKind.Error, status.Kind);
        Assert.Contains("disk full.", status.Message);
        Assert.True(setup.CanInstall);

        installer.Next();
        Assert.True(setup.InstallCommand.CanExecute(null));
        setup.InstallCommand.Execute(null);
        await Until(() => installer.Installs == 2);
        installer.Result.SetResult(new ModelInstallResult(1, 12));
        await setup.Completion;
        Assert.Equal(StatusKind.Info, status.Kind);
    }

    [Fact]
    public async Task The_cross_cancels_the_installation()
    {
        var installer = new FakeInstaller();
        var status = new StatusService();
        var setup = new ModelSetupService(installer, Models, Package, status, new ScriptedDialogs());

        setup.Start();
        await Until(() => installer.Installs == 1);
        setup.CancelCommand.Execute(null);
        await setup.Completion;

        Assert.True(installer.LastToken.IsCancellationRequested);
        Assert.False(setup.IsInstalling);
        Assert.Equal(StatusKind.Warning, status.Kind);
        Assert.Equal(Strings.ModelSetup_Cancelled, status.Message);
    }

    [Fact]
    public async Task Closing_while_installing_asks_and_keeps_the_window_or_cancels_and_waits()
    {
        var installer = new FakeInstaller();
        var dialogs = new ScriptedDialogs(0, 1);                            // «Продолжить установку», then «Прервать и закрыть»
        var setup = new ModelSetupService(installer, Models, Package, new StatusService(), dialogs);
        Assert.True(await setup.PrepareToCloseAsync());                     // nothing running: nothing asked
        Assert.Empty(dialogs.Asked);

        setup.Start();
        await Until(() => installer.Installs == 1);

        Assert.False(await setup.PrepareToCloseAsync());
        Assert.True(setup.IsInstalling);
        Assert.False(installer.LastToken.IsCancellationRequested);

        Assert.True(await setup.PrepareToCloseAsync());
        Assert.True(installer.LastToken.IsCancellationRequested);
        Assert.False(setup.IsInstalling);
        Assert.Equal(2, dialogs.Asked.Count);
    }

    /// <summary>As the export (D060 §5): the question itself never stops the installation — the window cancels it only once
    /// the unsaved-changes question agreed too, so a Cancel there leaves it running.</summary>
    [Fact]
    public async Task Agreeing_to_close_does_not_cancel_until_the_window_closes_for_good()
    {
        var installer = new FakeInstaller();
        var setup = new ModelSetupService(installer, Models, Package, new StatusService(), new ScriptedDialogs(1));
        setup.Start();
        await Until(() => installer.Installs == 1);

        Assert.True(await setup.AskToCloseAsync());
        Assert.False(installer.LastToken.IsCancellationRequested);
        Assert.True(setup.IsInstalling);

        await setup.CancelAndWaitAsync();
        Assert.True(installer.LastToken.IsCancellationRequested);
        Assert.False(setup.IsInstalling);
    }

    [Fact]
    public async Task The_settings_section_shows_the_state_and_the_install_button_only_with_the_package()
    {
        var settings = new InMemoryUserSettings();
        var installer = new FakeInstaller();
        var withPackage = new AppSettingsViewModel(settings, null, AppSettingsSection.Models,
            models: new ModelSetupService(installer, Models, Package, new StatusService()));
        Assert.True(withPackage.ShowsModels);
        Assert.True(withPackage.ModelsHavePackage);
        Assert.False(withPackage.ModelsHaveNoPackage);
        Assert.Contains(Models, withPackage.ModelsFolderText);
        await Until(() => withPackage.ModelsStateText == string.Format(Strings.AppSettings_ModelsMissing, 1));

        var installed = new AppSettingsViewModel(settings, null, AppSettingsSection.Models,
            models: new ModelSetupService(new FakeInstaller { State = new ModelInstallState(Array.Empty<string>()) }, Models, null, new StatusService()));
        Assert.False(installed.ModelsHavePackage);
        Assert.True(installed.ModelsHaveNoPackage);
        await Until(() => installed.ModelsStateText == string.Format(Strings.AppSettings_ModelsComplete, installed.Models!.TotalGigabytes));
        withPackage.Detach();
        installed.Detach();
    }
}
