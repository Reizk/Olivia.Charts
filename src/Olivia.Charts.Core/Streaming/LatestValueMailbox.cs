namespace Olivia.Core.Streaming;

public sealed record MailboxStatistics(
    long Published,
    long Delivered,
    long Coalesced,
    bool HasValue,
    bool NotificationPending,
    bool IsDelivering,
    bool IsPaused,
    bool IsDisposed,
    Exception? Error);

/// <summary>
/// A thread-safe capacity-one display mailbox, independent of collection and of any UI framework.
/// The scheduler must enqueue callbacks asynchronously, exactly once, on its serialized target context.
/// Values must be immutable. New publication replaces only the pending display value, never raw input.
/// Disposing invalidates queued callbacks and releases delegates; an already-started delivery may finish.
/// Delivery/scheduling errors stop publication and remain visible in Statistics.Error.
/// Hosts must still check their view generation and active state at the actual UI commit.
/// </summary>
public sealed class LatestValueMailbox<T> : IDisposable
{
    private readonly object _gate = new();
    private Action<Action>? _schedule;
    private Action<T>? _deliver;
    private T? _latest;
    private bool _hasValue, _pending, _delivering, _paused, _disposed;
    private long _published, _delivered, _coalesced, _ticket;
    private Exception? _error;

    public LatestValueMailbox(Action<Action> schedule, Action<T> deliver)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(deliver);
        _schedule = schedule;
        _deliver = deliver;
    }

    public MailboxStatistics Statistics
    {
        get
        {
            lock (_gate) return new(_published, _delivered, _coalesced, _hasValue, _pending,
                _delivering, _paused, _disposed, _error);
        }
    }

    /// <returns>False after disposal or a scheduler/delivery fault.</returns>
    public bool Publish(T value)
    {
        Action? schedule;
        lock (_gate)
        {
            if (_disposed || _error is not null) return false;
            _published++;
            if (_hasValue) _coalesced++;
            _latest = value;
            _hasValue = true;
            schedule = PrepareNotification();
        }
        schedule?.Invoke();
        lock (_gate) return _error is null;
    }

    public void Pause() { lock (_gate) _paused = true; }

    public void Resume()
    {
        Action? schedule;
        lock (_gate)
        {
            _paused = false;
            schedule = PrepareNotification();
        }
        schedule?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest = default;
            _hasValue = false;
            _pending = false;
            _ticket++;
            _schedule = null;
            _deliver = null;
        }
    }

    private Action? PrepareNotification()
    {
        if (_disposed || _error is not null || _paused || _pending || !_hasValue) return null;
        _pending = true;
        var ticket = ++_ticket;
        var scheduler = _schedule!;
        return () =>
        {
            // The queued delegate targets only this notification object. A nested lambda here
            // could retain the outer scheduler closure (and its UI references) after disposal.
            var notification = new ScheduledNotification(this, ticket);
            try { scheduler(notification.Invoke); }
            catch (Exception error) { RecordFault(error); }
            finally { notification.MarkScheduleReturned(); }
        };
    }

    private void RunNotification(long ticket)
    {
        T value;
        Action<T> deliver;
        lock (_gate)
        {
            if (_disposed || _error is not null || ticket != _ticket || !_pending || _delivering) return;
            if (_paused || !_hasValue)
            {
                _pending = false;
                return;
            }
            value = _latest!;
            _latest = default;
            _hasValue = false;
            _delivering = true;
            deliver = _deliver!;
        }
        var succeeded = false;
        try { deliver(value); succeeded = true; }
        catch (Exception error) { RecordFault(error); }
        finally
        {
            Action? schedule;
            lock (_gate)
            {
                if (succeeded) _delivered++;
                _delivering = false;
                _pending = false;
                schedule = PrepareNotification();
            }
            schedule?.Invoke();
        }
    }

    private void RecordFault(Exception error)
    {
        lock (_gate)
        {
            _error ??= error;
            _latest = default;
            _hasValue = false;
            _pending = false;
            _schedule = null;
            _deliver = null;
        }
    }

    private sealed class ScheduledNotification(LatestValueMailbox<T> owner, long ticket)
    {
        private readonly int _schedulingThread = Environment.CurrentManagedThreadId;
        private int _scheduleReturned;

        public void MarkScheduleReturned() => Volatile.Write(ref _scheduleReturned, 1);

        public void Invoke()
        {
            if (Volatile.Read(ref _scheduleReturned) == 0 && Environment.CurrentManagedThreadId == _schedulingThread)
            {
                owner.RecordFault(new InvalidOperationException("The scheduler must enqueue callbacks asynchronously."));
                return;
            }
            owner.RunNotification(ticket);
        }
    }
}
