using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Vision.Installation;
using Xunit;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// D070 §3 on the real models package: <c>AIVE_MODELS_PACKAGE</c> names a Portable folder's <c>ai-models</c> (or the stage's
/// models package of tools/distribution/New-Release.ps1). The application's installer copies every pinned file (about
/// 1.2 GB) into an empty temporary models folder with its SHA-256 checked, then a second run keeps them all; the folder is
/// removed afterwards. Without the variable the test does nothing.
/// </summary>
public sealed class PortableModelPackageTests
{
    [Fact]
    public async Task The_real_package_installs_into_an_empty_models_folder_and_a_second_run_keeps_every_file()
    {
        var package = Environment.GetEnvironmentVariable("AIVE_MODELS_PACKAGE");
        if (string.IsNullOrEmpty(package)) return;

        var target = Path.Combine(Path.GetTempPath(), "aive-real-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installer = new ModelPackageInstaller();
            Assert.False(installer.Check(target).IsComplete);
            var last = 0.0;
            var progress = new Progress<ModelInstallProgress>(p => last = p.Percent);

            var first = await installer.InstallAsync(package, target, progress, CancellationToken.None);
            Assert.Equal(new ModelInstallResult(13, 0), first);
            Assert.True(installer.Check(target).IsComplete);

            var second = await installer.InstallAsync(package, target, null, CancellationToken.None);
            Assert.Equal(new ModelInstallResult(0, 13), second);

            // The features' own check (size and SHA-256) accepts what was installed.
            foreach (var folder in ModelCatalog.Folders)
            {
                var status = await new HoldModelVerifier(Path.Combine(target, folder.Folder), folder.Files, folder.Folder).VerifyAsync(null, CancellationToken.None);
                Assert.True(status.IsReady, status.Message);
            }
            Assert.Empty(Directory.EnumerateFiles(target, "*.installing", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
