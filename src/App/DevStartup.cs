#if DEBUG
using AiVideoEditor.UI.Services;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AiVideoEditor.App;

/// <summary>
/// Development only — compiled into Debug builds, absent from Release: <c>--open-project &lt;folder&gt;</c> on the
/// command line opens that project once the main window is shown, through the same
/// <see cref="ProjectFileWorkflow.OpenAsync"/> the Open command uses after its folder picker. For manual test fixtures
/// (<c>tools/manual</c>, <c>docs/PHASE10_MANUAL_TEST_PLAN.md</c>) that must be opened without the native dialog.
/// </summary>
internal static class DevStartup
{
    public const string OpenProjectArgument = "--open-project";

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
}
#endif
