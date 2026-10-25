namespace Olivia.Core.Editing;
public sealed class SparklineSession
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private SparklineSnapshot _snapshot;
    private SparklineMode _mode;
    private bool _busy, _closed, _readOnly, _enabled = true;
    private long _callbackFailures;
    private EventHandler? _changed, _closedHandlers;
    public SparklineSession(SparklineSnapshot snapshot, SparklineMode mode = SparklineMode.Line)
    { _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)); if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode)); _mode = mode; }
    public SparklineSnapshot Snapshot { get { Verify(); return _snapshot; } }
    public SparklineMode Mode { get { Verify(); return _mode; } }
    public bool IsClosed { get { Verify(); return _closed; } }
    public bool IsReadOnly { get { Verify(); return _readOnly; } }
    public bool IsEnabled { get { Verify(); return _enabled; } }
    public bool CanChange { get { Verify(); return Writable; } }
    public long CallbackFailureCount { get { Verify(); return _callbackFailures; } }
    private bool Writable => !_closed && !_readOnly && _enabled;
    public event EventHandler Changed
    {
        add { Verify(); if (!_closed) _changed += value; }
        remove { Verify(); _changed -= value; }
    }
    public event EventHandler Closed
    {
        add { Verify(); if (!_closed) _closedHandlers += value; }
        remove { Verify(); _closedHandlers -= value; }
    }
    public bool SetMode(SparklineMode mode) => Mutate(() =>
    { if (!Writable || !Enum.IsDefined(mode) || mode == _mode) return false; _mode = mode; return true; });
    public bool Replace(SparklineSnapshot snapshot)
    {
        Verify(); ArgumentNullException.ThrowIfNull(snapshot);
        return Mutate(() => { if (!Writable || _snapshot.SameContent(snapshot)) return false; _snapshot = snapshot; return true; });
    }
    public bool SetReadOnly(bool value) => Mutate(() =>
    {
        if (_readOnly == value) return false; _readOnly = value; return true;
    });
    public bool SetEnabled(bool value) => Mutate(() =>
    {
        if (_enabled == value) return false; _enabled = value; return true;
    });
    public void Close()
    {
        Verify(); if (_closed) return;
        _closed = true; _changed = null;
        var handlers = _closedHandlers; _closedHandlers = null;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList()) Dispatch(handler);
    }
    private void Verify()
    {
        if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Use sparkline on its owning managed thread.");
    }
    private void Dispatch(EventHandler handler)
    {
        try { handler(this, EventArgs.Empty); }
        catch { if (_callbackFailures < long.MaxValue) _callbackFailures++; }
    }
    private bool Mutate(Func<bool> action)
    {
        Verify(); if (_closed || _busy) return false; _busy = true;
        try
        {
            if (!action()) return false;
            if (_changed is not null)
                foreach (EventHandler handler in _changed.GetInvocationList())
                { Dispatch(handler); if (_closed) break; }
            return true;
        }
        finally { _busy = false; }
    }
}
