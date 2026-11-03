using System.Globalization;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// «Настройки FIT…» (D051 §8.3, Step 37.6): the section «FIT» only — Beta (the collection of observations, on by
/// default; turning it off removes nothing), the current <c>StartCorrection</c>, the number of <c>used</c> records and the
/// reset (asks first). Each change is written to the per-user store at once; a file that can't be used says so.
/// </summary>
public sealed partial class FitSettingsViewModel : ViewModelBase
{
    private readonly IFitCalibrationStore _store;
    private readonly IDialogService? _dialogs;
    private readonly StatusService _status;
    private bool _syncing;

    public FitSettingsViewModel(IFitCalibrationStore store, StatusService status, IDialogService? dialogs = null)
    {
        _store = store;
        _status = status;
        _dialogs = dialogs;
        Show(store.Current);
    }

    /// <summary>Asks the window to close.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty] private bool _betaEnabled;

    [ObservableProperty] private string _estimateText = "";

    [ObservableProperty] private string _usedCountText = "";

    /// <summary>Why the file can't be used now, or empty.</summary>
    [ObservableProperty] private string _fileNote = "";

    [ObservableProperty] private bool _canChange;

    partial void OnBetaEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = SetBetaAsync(value);
    }

    private async Task SetBetaAsync(bool value)
    {
        if (!await _store.SetBetaAsync(value)) _status.Report(Strings.FitSettings_NotWritten);
        Show(_store.Current);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task Reset()
    {
        if (_dialogs is not null)
        {
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.FitSettings_ResetTitle, Message = Strings.FitSettings_ResetQuestion,
                Buttons = new[] { Strings.FitSettings_ResetConfirm, Strings.Common_Cancel }
            });
            if (choice != 0) return;
        }
        if (await _store.ResetAsync()) _status.Report(Strings.FitSettings_ResetDone);
        else _status.Report(Strings.FitSettings_NotWritten);
        Show(_store.Current);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnCanChangeChanged(bool value) => ResetCommand.NotifyCanExecuteChanged();

    private void Show(FitCalibrationSnapshot snapshot)
    {
        _syncing = true;
        try
        {
            BetaEnabled = snapshot.BetaEnabled;
            EstimateText = string.Format(CultureInfo.CurrentCulture, Strings.FitSettings_Estimate,
                snapshot.EstimateTicks / (double)TimeSpan.TicksPerSecond);
            UsedCountText = string.Format(CultureInfo.CurrentCulture, Strings.FitSettings_UsedCount, snapshot.UsedCount);
            FileNote = snapshot.FileState switch
            {
                FitCalibrationFileState.NewerFormat => Strings.FitSettings_NewerFile,
                FitCalibrationFileState.Unreadable => Strings.FitSettings_UnreadableFile,
                _ => ""
            };
            CanChange = snapshot.FileState == FitCalibrationFileState.Usable;
        }
        finally
        {
            _syncing = false;
        }
    }
}
