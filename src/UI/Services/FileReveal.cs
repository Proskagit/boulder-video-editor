namespace AiVideoEditor.UI.Services;

/// <summary>Shows a file in Explorer with the file selected (D068 item 13: «Показать в папке» after an export).</summary>
public static class FileReveal
{
    public static void Select(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no Explorer to show it with: the path is in the message
        }
    }
}
