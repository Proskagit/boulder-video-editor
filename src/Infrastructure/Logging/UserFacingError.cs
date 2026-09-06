namespace AiVideoEditor.Infrastructure.Logging;

/// <summary>
/// A message safe and clear enough to show directly in the UI, paired with the
/// full technical detail that should only ever go to the log — never to a dialog.
/// </summary>
public sealed record UserFacingError(string UserMessage, string TechnicalDetail);

/// <summary>
/// Maps low-level failures (missing files, FFmpeg process errors, unsupported
/// codecs, cancelled operations, corrupt project files, ...) to a short message a
/// non-technical user can act on. Every service that can fail should go through
/// this rather than surfacing raw exception text.
/// </summary>
public static class ErrorTranslator
{
    public static UserFacingError Translate(Exception ex, string context)
    {
        var technical = $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";

        var userMessage = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                "A required file could not be found. It may have been moved, renamed, or deleted.",

            UnauthorizedAccessException =>
                "The file couldn't be accessed. Check that it isn't open in another program and that you have permission to read it.",

            OperationCanceledException =>
                "The operation was cancelled.",

            FfmpegNotFoundException =>
                "FFmpeg could not be found. Please check your installation and try again.",

            UnsupportedMediaException ume =>
                $"This file's format ({ume.CodecOrFormat}) isn't supported yet.",

            CorruptProjectFileException =>
                "This project file appears to be damaged and couldn't be opened. A backup may be available in the project's cache folder.",

            _ => "Something went wrong. The details have been saved to the application log."
        };

        return new UserFacingError(userMessage, technical);
    }
}

public sealed class FfmpegNotFoundException(string message) : Exception(message);

public sealed class UnsupportedMediaException(string codecOrFormat, string message) : Exception(message)
{
    public string CodecOrFormat { get; } = codecOrFormat;
}

public sealed class CorruptProjectFileException(string message, Exception? inner = null) : Exception(message, inner);
