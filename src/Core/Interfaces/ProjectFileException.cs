namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// A project file could not be read or written: it is not a project file, is damaged,
/// was written by a newer version of the application, or the disk operation failed.
/// <see cref="Exception.Message"/> is short and user-facing; details are in
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class ProjectFileException : Exception
{
    public ProjectFileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
