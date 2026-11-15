using AiVideoEditor.Core.Export;

namespace AiVideoEditor.UI.Services;

/// <summary>The encoders a dialog offers (D059 §3): the check started at app launch, waited for at most
/// <see cref="Wait"/> — a check still running offers libx264 only (the export itself waits for the full result).</summary>
public static class EncoderAvailability
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    public static async Task<ExportEncoderAvailability> GetAsync(IExportEncoderCapabilities? capabilities)
    {
        if (capabilities is null) return ExportEncoderAvailability.SoftwareOnly;
        try
        {
            return await capabilities.GetAsync().WaitAsync(Wait);
        }
        catch (TimeoutException)
        {
            return ExportEncoderAvailability.SoftwareOnly;
        }
    }
}
