#if DEBUG
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Media;
using AiVideoEditor.Project.Research;
using AiVideoEditor.UI.Research;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Research;
using AiVideoEditor.UI.Views.Research;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AiVideoEditor.App;

/// <summary>
/// Development only — compiled into Debug builds, absent from Release: <c>--open-project &lt;folder&gt;</c> on the
/// command line opens that project once the main window is shown, through the same
/// <see cref="ProjectFileWorkflow.OpenAsync"/> the Open command uses after its folder picker. For manual test fixtures
/// (<c>tools/manual</c>, <c>docs/PHASE10_MANUAL_TEST_PLAN.md</c>) that must be opened without the native dialog.
/// <para>
/// Step 35.3R1: <c>--coros-reference &lt;study file&gt;</c> opens the window «Проверка синхронизации COROS» in its
/// reference-marking mode beside the main window, over the main window's Source viewer, with that study (required; the
/// window never creates one). Used with <c>--open-project &lt;the set's project folder&gt;</c>.
/// </para>
/// </summary>
internal static class DevStartup
{
    public const string OpenProjectArgument = "--open-project";
    public const string CorosReferenceArgument = "--coros-reference";

    public static void OpenProjectFromArguments(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var index = Array.IndexOf(args, OpenProjectArgument);
        if (index < 0 || desktop.MainWindow is not { } window) return;
        if (index + 1 >= args.Length)
        {
            Log.Warning("{Argument} needs a project folder.", OpenProjectArgument);
            return;
        }

        var folder = args[index + 1];
        var files = App.Services.GetRequiredService<ProjectFileWorkflow>();
        window.Opened += async (_, _) =>
        {
            Log.Information("Development: opening the project in {Folder} from the command line.", folder);
            await files.OpenAsync(folder);
        };
    }

    /// <summary>The study file given after <see cref="CorosReferenceArgument"/>, or null (absent, or no path after it).</summary>
    public static string? CorosReferencePath(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf(CorosReferenceArgument);
        return index >= 0 && index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    public static void OpenCorosReferenceFromArguments(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        if (Array.IndexOf(args, CorosReferenceArgument) < 0 || desktop.MainWindow is not { } window) return;
        if (CorosReferencePath(args) is not { } path)
        {
            Log.Warning("{Argument} needs the path of a study file; no study is created.", CorosReferenceArgument);
            return;
        }

        window.Opened += async (_, _) =>
        {
            if (window.DataContext is not MainWindowViewModel main) return;
            var study = new SyncStudyService(new FileFingerprintService(), () => DateTimeOffset.UtcNow);
            var model = new SyncReferenceViewModel(study, new PreviewSourceView(main.Preview), App.Services.GetRequiredService<IProjectService>());
            new SyncReferenceWindow { DataContext = model }.Show();
            Log.Information("Development: opening the COROS reference marking with the study {Path}.", path);
            if (!await model.OpenStudyAsync(path)) Log.Warning("The study {Path} could not be opened; nothing is created.", path);
        };
    }
}
#endif
