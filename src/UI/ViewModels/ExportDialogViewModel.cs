using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// What the export dialog decided: the output file and (D059 §4) the export settings — the profile, the encoder and the
/// «Пользовательский» fields; null = the project's settings unchanged.
/// </summary>
public sealed record ExportDialogResult(string OutputPath)
{
    public ExportEncoding? Encoding { get; init; }
}

/// <summary>
/// The owner's acceptance of 2026-11-10 (Phase 38): the main «Экспорт» opens one dialog — the output folder and file name
/// («Обзор…» opens the save-file picker), a short line about the output (size, frame rate, length, MP4), and the YouTube
/// timestamps of what is exported as a small read-only text to copy (<see cref="YouTubeTimestamps"/>: the whole sequence,
/// or with an In / Out range export the range, its times from In — the timestamps describe the file being made).
/// </summary>
public sealed partial class ExportDialogViewModel : ViewModelBase
{
    private readonly Func<string, string, Task<string?>>? _browse;

    /// <param name="output">The output's format (<see cref="ExportOutput.For"/>).</param>
    /// <param name="suggestedPath">The path offered first (the last export's, else the project's folder and name).</param>
    /// <param name="browse">Opens the save-file picker at (folder, name); returns the chosen path or null.</param>
    /// <param name="encoding">The project's export settings (D059 §4), shown in the settings editor; null = the default.</param>
    /// <param name="encoders">Which encoders work here (NVENC offered only when confirmed); null = libx264 only.</param>
    public ExportDialogViewModel(Sequence sequence, FrameRate rate, ExportOutput output, string suggestedPath,
        ExportRange? range = null, Func<string, string, Task<string?>>? browse = null, ExportEncoding? encoding = null,
        ExportEncoderAvailability? encoders = null)
    {
        _browse = browse;
        Settings = new ExportEncodingEditorViewModel(encoding ?? ExportEncoding.Default, encoders, output);
        IsRange = range is not null;
        Timestamps = range is { } r
            ? YouTubeTimestamps.Text(sequence, rate, MediaTime.FromFrame(r.FirstFrame, rate), MediaTime.FromFrame(r.EndFrame, rate))
            : YouTubeTimestamps.Text(sequence, rate);
        var fps = output.FrameRate.ToDouble().ToString("0.###", CultureInfo.CurrentCulture);
        Info = string.Format(Strings.ExportDialog_Info, output.Size.Width, output.Size.Height, fps, TimeFormat.ToShortString(output.Duration))
            + (IsRange ? " · " + Strings.ExportDialog_InfoRange : "");
        SetPath(suggestedPath);
    }

    public string Info { get; }

    /// <summary>The export settings (D059 §4) with the expected file size of the output.</summary>
    public ExportEncodingEditorViewModel Settings { get; }

    public bool IsRange { get; }

    /// <summary>The YouTube timestamps of the exported part, one line per route.</summary>
    public string Timestamps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _folder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _fileName = "";

    /// <summary>The folder and the file name, the <c>.mp4</c> added when the name has no extension; null while either is empty
    /// or not a usable path.</summary>
    public string? OutputPath
    {
        get
        {
            var name = FileName.Trim();
            var folder = Folder.Trim();
            if (name.Length == 0 || folder.Length == 0) return null;
            if (!Path.HasExtension(name)) name += ExportFormat.FileExtension;
            try
            {
                return Path.Combine(folder, name);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>The dialog's answer: the result on «Экспортировать», null on «Отмена» / closing the window.</summary>
    public event EventHandler<ExportDialogResult?>? Closed;

    /// <summary>Raised by «Копировать» with the timestamps; the view puts them on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    private void SetPath(string path)
    {
        try
        {
            Folder = Path.GetDirectoryName(path) ?? "";
            FileName = Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            FileName = path;
        }
    }

    public bool CanBrowse => _browse is not null;

    [RelayCommand]
    private async Task Browse()
    {
        if (_browse is null) return;
        if (await _browse(Folder, FileName) is { } chosen) SetPath(chosen);
    }

    private bool CanExport() => OutputPath is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (OutputPath is { } path) Closed?.Invoke(this, new ExportDialogResult(path) { Encoding = Settings.Value });
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, null);

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, Timestamps);
}
