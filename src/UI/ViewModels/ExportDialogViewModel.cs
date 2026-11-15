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
/// D060 §3: the resolution of the exported file — 4K UHD / Full HD / HD / SD at the canvas's aspect ratio
/// (<see cref="ExportResolutions"/>), each with its size; «Как в проекте» with the canvas's size is offered first only when
/// the canvas is none of them. The canvas and the project settings never change. Opens on the project's choice, or — none
/// stored — on the preset that is the canvas's size (or «Как в проекте»), i.e. exactly the earlier output.
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
        ExportEncoderAvailability? encoders = null)
    {
        _browse = browse;
        _canvas = output.Size;
        _duration = output.Duration;
        _fps = output.FrameRate.ToDouble().ToString("0.###", CultureInfo.CurrentCulture);
        encoding ??= ExportEncoding.Default;
        Settings = new ExportEncodingEditorViewModel(encoding, encoders, output);
        IsRange = range is not null;
        Resolutions = BuildResolutions(_canvas);
        var initial = encoding.Resolution == ExportResolution.Project
            ? ExportResolutions.PresetMatching(_canvas) ?? ExportResolution.Project
            : encoding.Resolution;
        _selectedResolution = Resolutions.FirstOrDefault(r => r.Value == initial) ?? Resolutions[0];
        Settings.Resolution = _selectedResolution.Value;
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
            var size = ExportResolutions.SizeFor(SelectedResolution.Value, _canvas);
            return string.Format(Strings.ExportDialog_Info, size.Width, size.Height, _fps, TimeFormat.ToShortString(_duration))
                + (IsRange ? " · " + Strings.ExportDialog_InfoRange : "");
        }
    }

    /// <summary>D060 §3: the resolutions offered for this canvas (largest first).</summary>
    public IReadOnlyList<ExportChoice<ExportResolution>> Resolutions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Info))]
    private ExportChoice<ExportResolution> _selectedResolution;

    partial void OnSelectedResolutionChanged(ExportChoice<ExportResolution> value)
    {
        if (value is not null) Settings.Resolution = value.Value;
    }

    /// <summary>The presets at the canvas's aspect ratio, each with its size; «Как в проекте» first when the canvas is no
    /// preset's size.</summary>
    public static IReadOnlyList<ExportChoice<ExportResolution>> BuildResolutions(FrameSize canvas)
    {
        var list = new List<ExportChoice<ExportResolution>>();
        if (ExportResolutions.PresetMatching(canvas) is null)
            list.Add(new ExportChoice<ExportResolution>(ExportResolution.Project, Strings.ExportResolution_Project,
                string.Format(Strings.ExportResolution_Size, canvas.Width, canvas.Height)));
        foreach (var preset in ExportResolutions.Presets)
        {
            var size = ExportResolutions.SizeFor(preset, canvas);
            var detail = string.Format(Strings.ExportResolution_Size, size.Width, size.Height);
            if (size == canvas) detail += " · " + Strings.ExportResolution_AsProject;
            list.Add(new ExportChoice<ExportResolution>(preset, ResolutionName(preset), detail));
        }
        return list;
    }

    public static string ResolutionName(ExportResolution resolution) => resolution switch
    {
        ExportResolution.Uhd4K => Strings.ExportResolution_Uhd4K,
        ExportResolution.FullHd => Strings.ExportResolution_FullHd,
        ExportResolution.Hd => Strings.ExportResolution_Hd,
        ExportResolution.Sd => Strings.ExportResolution_Sd,
        _ => Strings.ExportResolution_Project
    };

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
        // D060 §3: a preset of exactly the canvas's size is stored as the canvas — the same output, nothing new in the file.
        if (OutputPath is { } path) Closed?.Invoke(this, new ExportDialogResult(path) { Encoding = ExportResolutions.Normalize(Settings.Value, _canvas) });
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, null);

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, Timestamps);
}
