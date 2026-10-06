namespace AiVideoEditor.Project.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when the test advances the time — synchronously, on the
/// calling thread, in due order. Every timer it created stays listed, so a test can also fire the callback of a
/// disposed one, as the thread pool may run a callback that was queued just before <see cref="ITimer.Dispose"/>.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<ManualTimer> Timers => _timers;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the time forward, firing every timer that falls due on the way (a periodic one as often as it
    /// falls due), each at its own due time.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        var end = _now + by;
        while (true)
        {
            var next = _timers.Where(t => t.DueAt is { } due && due <= end).MinBy(t => t.DueAt);
            if (next is null) break;
            _now = next.DueAt!.Value;
            next.Elapse();
        }
        _now = end;
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>When it fires next; null when disposed or not scheduled.</summary>
        public DateTimeOffset? DueAt { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed) return false;
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
            return true;
        }

        /// <summary>Runs the callback whatever the timer's state — a callback the runtime had already queued.</summary>
        public void InvokeCallback() => _callback(_state);

        internal void Elapse()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            InvokeCallback();
        }

        public void Dispose()
        {
            IsDisposed = true;
            DueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A UI-thread stand-in: <see cref="Post"/> only queues; the test runs the queue when it chooses.</summary>
internal sealed class QueueingSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    public void RunPending()
    {
        while (_queue.TryDequeue(out var item))
            item.Callback(item.State);
    }
}
