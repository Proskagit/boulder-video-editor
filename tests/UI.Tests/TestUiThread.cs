using System.Collections.Concurrent;

namespace AiVideoEditor.UI.Tests;

/// <summary>A UI thread stand-in: one dedicated thread that runs everything posted to its synchronization context, as the
/// Avalonia dispatcher does (continuations of an await started on it come back to it) — for services that, like the app's,
/// rely on running on one thread.</summary>
internal sealed class TestUiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public TestUiThread()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted) _queue.Add((d, state));
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread; the task completes when it does.</summary>
    public Task Run(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { await work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
