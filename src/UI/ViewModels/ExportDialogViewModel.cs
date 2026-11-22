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
/// <para>
/// D076: the preset on top, its every field — the file type, the codec, the resolution (D060 §3) … — in «Подробные
/// настройки» (<see cref="ExportEncodingEditorViewModel"/>); the file's extension follows the file type.
/// </para>
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
        ExportEncoderAvailability? encoders = null, Core.Interfaces.IUserSettingsService? userSettings = null)
    {
        _browse = browse;
        _canvas = output.Size;
        _duration = output.Duration;
        _fps = output.FrameRate.ToDouble().ToString("0.###", CultureInfo.CurrentCulture);
        encoding ??= ExportEncoding.Default;
        // D076: the resolution is the preset's (in the settings editor); the file's extension follows the chosen file type.
        Settings = new ExportEncodingEditorViewModel(encoding, encoders, output, userSettings: userSettings);
        Settings.Changed += OnSettingsChanged;
        _container = Settings.Value.Container;
        IsRange = range is not null;
        Timestamps = range is { } r
            ? YouTubeTimestamps.Text(sequence, rate, MediaTime.FromFrame(r.FirstFrame, rate), MediaTime.FromFrame(r.EndFrame, rate))
            : YouTubeTimestamps.Text(sequence, rate);
        SetPath(suggestedPath);
    }

    private readonly FrameSize _canvas;
    private readonly MediaTime _duration;
    private readonly string _fps;

    /// <summary>The output's line: its size (the chosen resolution), frame rate, length and format.</summary>
    public string Info
    {
        get
        {
            var size = ExportResolutions.SizeFor(Settings.Resolution, _canvas);
            var e = Settings.Value;
            return string.Format(Strings.ExportDialog_Info, size.Width, size.Height, _fps, TimeFormat.ToShortString(_duration),
                    e.Container.ToString().ToUpperInvariant(), ExportEncodingEditorViewModel.CodecOptions.Single(c => c.Value == e.Codec).Label.Split(' ')[0])
                + (IsRange ? " · " + Strings.ExportDialog_InfoRange : "");
        }
    }

    private ExportContainer _container;

    /// <summary>D076: a new file type renames the file's extension (.mp4 → .mov / .mkv); the output's line follows the
    /// resolution.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Info));
        var container = Settings.Value.Container;
        if (container == _container) return;
        _container = container;
        var name = FileName.Trim();
        if (ExportFormat.ContainerOf(name) is not null) FileName = Path.ChangeExtension(name, ExportFormat.ExtensionOf(container));
    }

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

    /// <summary>The folder and the file name, the chosen file type's extension added when the name has no extension; null while either is empty
    /// or not a usable path.</summary>
    public string? OutputPath
    {
        get
        {
            var name = FileName.Trim();
            var folder = Folder.Trim();
            if (name.Length == 0 || folder.Length == 0) return null;
            var extension = ExportFormat.ExtensionOf(Settings.Value.Container);
            if (!Path.HasExtension(name)) name += extension;
            // D076: the chosen file type wins over another video extension typed or picked (.mp4 / .mov / .mkv).
            else if (ExportFormat.ContainerOf(name) is not null) name = Path.ChangeExtension(name, extension);
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

    /// <summary>A path, and a codec this computer can encode (D076).</summary>
    private bool CanExport() => OutputPath is not null && Settings.CanEncode;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        // D060 §3: a preset of exactly the canvas's size is stored as the canvas — the same output, nothing new in the file.
        if (OutputPath is not { } path) return;
        Settings.Detach();
        Closed?.Invoke(this, new ExportDialogResult(path) { Encoding = ExportResolutions.Normalize(Settings.Value, _canvas) });
    }

    [RelayCommand]
    private void Cancel()
    {
        Settings.Detach();
        Closed?.Invoke(this, null);
    }

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, Timestamps);
}
