using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project.Persistence;

/// <summary>The global settings (D057 §3.8) in <c>settings.json</c> of the user's configuration folder.</summary>
public sealed class UserSettingsService : IUserSettingsService
{
    public const string FormatId = "AiVideoEditor.UserSettings";
    public const int CurrentFormatVersion = 1;

    private readonly UserJsonFile<UserSettings> _file;

    public UserSettingsService(string filePath, ILogger<UserSettingsService> logger)
    {
        _file = new UserJsonFile<UserSettings>(filePath, FormatId, CurrentFormatVersion, logger);
        Current = _file.Read().Normalized();
    }

    public UserSettings Current { get; private set; }

    public UserFileState FileState => _file.State;

    public event EventHandler? Changed;

    public void Update(Func<UserSettings, UserSettings> change)
    {
        var next = change(Current).Normalized();
        if (next == Current) return;
        Current = next;
        _file.Write(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The working layout (D057 §3.1, §3.4) in <c>ui-state.json</c> of the user's configuration folder. A burst of
/// changes (a splitter dragged, sections toggled) is written once, <see cref="WriteDelay"/> after the last.</summary>
public sealed class UiStateService : IUiStateService, IDisposable
{
    public const string FormatId = "AiVideoEditor.UiState";
    public const int CurrentFormatVersion = 1;

    private readonly UserJsonFile<UiState> _file;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Timer _timer;
    private bool _pending;

    public UiStateService(string filePath, ILogger<UiStateService> logger)
    {
        _file = new UserJsonFile<UiState>(filePath, FormatId, CurrentFormatVersion, logger);
        Current = _file.Read();
        _timer = new Timer(_ => Flush());
    }

    public static TimeSpan WriteDelay { get; } = TimeSpan.FromSeconds(1);

    public UiState Current { get; private set; }

    public UserFileState FileState => _file.State;

    public void Update(Func<UiState, UiState> change)
    {
        lock (_gate)
        {
            var next = change(Current);
            if (next == Current) return;
            Current = next;
            _pending = true;
            _timer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        lock (_writeGate)
        {
            UiState state;
            lock (_gate)
            {
                if (!_pending) return;
                _pending = false;
                state = Current;
            }
            _file.Write(state);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
