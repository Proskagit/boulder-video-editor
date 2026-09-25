using AiVideoEditor.Infrastructure.Logging;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.3: the app keeps no backup of project files, so the message for a damaged project must say what
/// happened and promise nothing to restore from.
/// </summary>
public sealed class ErrorTranslatorTests
{
    [Fact]
    public void A_damaged_project_is_described_without_promising_a_backup()
    {
        var error = ErrorTranslator.Translate(new CorruptProjectFileException("unexpected end of JSON"), "Opening a project");

        Assert.Equal("This project file is damaged or can't be read, so the project couldn't be opened.", error.UserMessage);
        foreach (var promise in new[] { "backup", "cache", "recover", "restore", "copy" })
            Assert.DoesNotContain(promise, error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unexpected end of JSON", error.TechnicalDetail); // the detail still goes to the log
    }
}
