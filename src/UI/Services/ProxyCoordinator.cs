using System.Globalization;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// D071 §3–§4, amendment 2: the one proxy (1080p) of each of the current project's 4K / HEVC videos — found in the cache, or
/// made in the background one at a time (<see cref="UserSettings.MakeProxies"/>), the rules of <see cref="MediaCacheCoordinator{T}"/>
/// (generations per project, once per asset). The Preview's quality never asks for another proxy: it only decides whether the
/// Preview takes it (<see cref="ProxyFor"/>, <see cref="ProxyReady"/>); the status bar shows the one being made.
/// </summary>
public sealed class ProxyCoordinator : MediaCacheCoordinator<string>
{
    private readonly IProxyStore _store;
    private readonly IUserSettingsService _settings;
    private readonly IProjectService _projects;
    private readonly SynchronizationContext? _context;
    private bool _make;
    private readonly Dictionary<Guid, HdrConversion?> _colour = new();
    private readonly object _buildLock = new();
    private CancellationTokenSource? _build;   // the making running now: cancelled at once, from any thread

    public ProxyCoordinator(IProxyStore store, IProjectService projects, IUserSettingsService settings, ILogger<ProxyCoordinator> logger)
        : base(projects, maxConcurrent: 1, "proxy", logger)
    {
        _store = store;
        _settings = settings;
        _projects = projects;
        _context = SynchronizationContext.Current;
        _make = settings.Current.MakeProxies;
        settings.Changed += (_, _) => OnSettingsChanged();
        Progress = new ProxyProgress(Cancel);
        // D033 T3: a colour found later by the background probe changes the picture — the proxy is asked for again (another key).
        projects.ProjectChanged += (_, _) => { _colour.Clear(); RestartWhereColourChanged(); };
        projects.MediaAssetsChanged += (_, _) => RestartWhereColourChanged();
        RestartWhereColourChanged();
        RequestAll();
    }

    /// <summary>The proxy being made now (the status bar's indicator).</summary>
    public ProxyProgress Progress { get; }

    /// <summary>Raised (on the caller's context) with the asset id when its proxy is ready.</summary>
    public event EventHandler<Guid>? ProxyReady
    {
        add => Ready += value;
        remove => Ready -= value;
    }

    /// <summary>The ready proxy of an asset of the current project, or null.</summary>
    public string? ProxyFor(Guid assetId) => Get(assetId);

    /// <summary>The settings' «Удалить прокси»: every proxy not being read is removed and the project's are asked again.</summary>
    public long ClearAll()
    {
        Cancel();
        var freed = _store.Clear();
        RestartAll();
        return freed;
    }

    public long CacheBytes() => _store.SizeBytes();

    private void OnSettingsChanged()
    {
        var make = _settings.Current.MakeProxies;
        if (make == _make) return;   // the quality alone changes nothing here (amendment 2)
        _make = make;
        if (!_make) Cancel();
        else RestartAll(dropResults: false);   // asks again the files that got none while it was off
    }

    private void RestartWhereColourChanged()
    {
        var changed = new List<Guid>();
        foreach (var asset in _projects.Current.MediaAssets.Where(a => a.Kind == MediaKind.Video && a.Metadata is not null))
        {
            var colour = HdrColor.Classify(asset.Kind, asset.Metadata).Conversion;
            if (_colour.TryGetValue(asset.Id, out var before) && before != colour) changed.Add(asset.Id);
            _colour[asset.Id] = colour;
        }
        if (changed.Count > 0) Restart(changed, dropResults: true);
    }

    private void RestartAll(bool dropResults = true) =>
        Restart(_projects.Current.MediaAssets.Select(a => a.Id).ToList(), dropResults);

    /// <summary>The proxy being made is given up (the user cancelled, the switch turned off, the cache is cleared); the assets
    /// are asked again only by a restart.</summary>
    private void Cancel()
    {
        lock (_buildLock)
        {
            try { _build?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    protected override IEnumerable<MediaAsset> Candidates(Project project) => project.MediaAssets;

    protected override bool MayHave(MediaAsset asset) => PreviewDecoding.NeedsProxy(asset);

    protected override string CurrentFolder => _store.Folder;

    protected override string? TryGetCached(MediaAsset asset, string folder) => _store.TryGet(asset);

    protected override async Task<string?> GetOrCreateAsync(MediaAsset asset, string folder, CancellationToken ct)
    {
        if (!_make || !PreviewDecoding.NeedsProxy(asset)) return null;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_buildLock) _build = own;
        // The switch turned off while this one waited for its turn: given up before it starts.
        if (!_make) own.Cancel();
        var name = Path.GetFileName(asset.FilePath);
        Post(() => Progress.Begin(name, own));
        try
        {
            return await _store.GetOrCreateAsync(asset, new Reporter(this), own.Token);
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;   // given up by the user or the switch: not asked again until a restart
        }
        catch (ProxyBuildException)
        {
            throw;          // logged by the base («No proxy for …»); the Preview stays on the source
        }
        finally
        {
            lock (_buildLock) if (ReferenceEquals(_build, own)) _build = null;
            Post(() => Progress.End(own));
        }
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    /// <summary>The builder's progress, sent to the caller's context.</summary>
    private sealed class Reporter(ProxyCoordinator owner) : IProgress<double>
    {
        private int _lastPercent = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Floor(Math.Clamp(value, 0, 1) * 100);
            if (Interlocked.Exchange(ref _lastPercent, percent) == percent) return;
            owner.Post(() => owner.Progress.Percent = percent);
        }
    }
}

/// <summary>D071 §3: the status bar's indicator of the proxy being made.</summary>
public sealed partial class ProxyProgress : ObservableObject
{
    private CancellationTokenSource? _current;

    public ProxyProgress(Action cancel)
    {
        CancelCommand = new RelayCommand(cancel, () => IsBuilding);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactText))]
    private bool _isBuilding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactText))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactText))]
    private int _percent;

    public RelayCommand CancelCommand { get; }

    public string CompactText => string.Format(CultureInfo.CurrentCulture, Strings.Proxy_Compact, FileName, Percent);

    internal void Begin(string fileName, CancellationTokenSource cancellation)
    {
        _current = cancellation;
        FileName = fileName;
        Percent = 0;
        IsBuilding = true;
        CancelCommand.NotifyCanExecuteChanged();
    }

    internal void End(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_current, cancellation)) return;
        _current = null;
        IsBuilding = false;
        CancelCommand.NotifyCanExecuteChanged();
    }
}
