namespace AiVideoEditor.UI.Common;

/// <summary>Formats a byte count for display, e.g. "842 MB". Binary (1024-based)
/// units labeled the way most file managers show them.</summary>
public static class FileSizeFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string ToShortString(long bytes)
    {
        if (bytes <= 0) return "0 B";

        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < Units.Length - 1)
        {
            size /= 1024;
            order++;
        }

        // Whole numbers for KB and up (matches the "842 MB" style in the spec);
        // bytes themselves are always shown as whole numbers too.
        return order == 0 ? $"{bytes} {Units[order]}" : $"{size:0} {Units[order]}";
    }
}
