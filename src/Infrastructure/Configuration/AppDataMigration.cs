namespace AiVideoEditor.Infrastructure.Configuration;

/// <summary>
/// D068 §1: the per-user folder was renamed with the product (<c>%LOCALAPPDATA%\AiVideoEditor</c> →
/// <c>%LOCALAPPDATA%\BoulderVideoEditor</c>). At startup, before anything is written, everything still in the old folder is
/// moved into the new one: an entry missing there is moved whole (a rename on the same disk — the models' gigabyte is not
/// copied), a folder present in both is merged entry by entry, and a file present in both stays where it is (the new one
/// wins, nothing is overwritten or deleted). The old folder is removed only when it ends up empty. A failure (a file in
/// use by a running 1.0) leaves that entry in the old folder for the next start; startup never fails because of it.
/// </summary>
public static class AppDataMigration
{
    /// <summary>The result, logged once the logger exists (the move runs before it, since the logs live there).</summary>
    public sealed record Result(int Moved, int Kept, IReadOnlyList<string> Failures, bool OldFolderRemoved)
    {
        public static Result Nothing { get; } = new(0, 0, Array.Empty<string>(), false);
    }

    /// <summary>Moves the legacy folder's content into <see cref="AppPaths.AppDataRoot"/>.</summary>
    public static Result Run() => Run(AppPaths.LegacyAppDataRoot, AppPaths.AppDataRoot);

    public static Result Run(string oldRoot, string newRoot)
    {
        if (!Directory.Exists(oldRoot))
            return Result.Nothing;

        var failures = new List<string>();
        var moved = 0;
        var kept = 0;
        try
        {
            if (!Directory.Exists(newRoot))
            {
                Directory.Move(oldRoot, newRoot);
                return new Result(1, 0, failures, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The whole folder could not be renamed (an open file) — fall through to the entry-by-entry move.
            Directory.CreateDirectory(newRoot);
        }

        Merge(oldRoot, newRoot, ref moved, ref kept, failures);

        var removed = false;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(oldRoot).Any())
            {
                Directory.Delete(oldRoot);
                removed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{oldRoot}: {ex.Message}");
        }

        return new Result(moved, kept, failures, removed);
    }

    private static void Merge(string from, string to, ref int moved, ref int kept, List<string> failures)
    {
        foreach (var source in Directory.EnumerateFileSystemEntries(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(source));
            var isFolder = Directory.Exists(source);
            try
            {
                if (isFolder && Directory.Exists(target))
                {
                    Merge(source, target, ref moved, ref kept, failures);
                    if (!Directory.EnumerateFileSystemEntries(source).Any())
                        Directory.Delete(source);
                    continue;
                }

                if (Directory.Exists(target) || File.Exists(target))
                {
                    kept++;
                    continue;
                }

                if (isFolder)
                    Directory.Move(source, target);
                else
                    File.Move(source, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{source}: {ex.Message}");
            }
        }
    }
}
