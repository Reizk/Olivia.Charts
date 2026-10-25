using System.Diagnostics.CodeAnalysis;

namespace Olivia.Core.Snapshots;

/// <summary>Bounds store-owned versions, logical items and leases, not bytes or caller-owned copies.</summary>
public sealed record SnapshotBudget
{
    public SnapshotBudget(int maximumVersions, long maximumItems, int maximumLeases)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumVersions, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumItems);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLeases, 1);
        MaximumVersions = maximumVersions;
        MaximumItems = maximumItems;
        MaximumLeases = maximumLeases;
    }

    public int MaximumVersions { get; }
    public long MaximumItems { get; }
    public int MaximumLeases { get; }
}

public enum SnapshotOwner { Query, Display, Pending }
public enum SnapshotPublishStatus { Published, BudgetExceeded, Stopped }
public enum SnapshotAcquireStatus { Acquired, Expired, Busy, Stopped }

/// <summary>Only an acquired lease exposes a value; all unsuccessful results have a null lease.</summary>
public readonly record struct SnapshotAcquireResult<T>(SnapshotAcquireStatus Status, SnapshotLease<T>? Lease = null)
    where T : class;

/// <summary>
/// An atomic view of store ownership, including expired leases awaiting disposal. Published counts
/// successful publications, Rejected counts budget-rejected publications, and Expired counts expired
/// acquisition attempts. High-water marks and counters are cumulative across Reset.
/// </summary>
public sealed record SnapshotStoreStatistics(
    SnapshotBudget Budget,
    int RetainedVersions,
    long RetainedItems,
    int ActiveLeases,
    int QueryLeases,
    int DisplayLeases,
    int PendingLeases,
    int VersionHighWaterMark,
    long ItemHighWaterMark,
    int LeaseHighWaterMark,
    long Published,
    long Rejected,
    long Expired,
    long BusyAcquisitions,
    bool IsStopped);

/// <summary>
/// Thread-safe ownership of immutable snapshots: retain the latest version plus any pinned versions.
/// Replacing an unleased latest version releases it immediately. No pinned version is evicted to
/// make a publication fit. Versions must increase across successful publications, including resets.
/// A budget-rejected publication leaves the latest version unchanged and may be retried.
///
/// Values must remain immutable, and consumers must scope access to a lease and dispose every lease.
/// Counts cover only values retained by this store; producer references, escaped Value references,
/// query output copies, item object graphs and framework/GPU caches are outside these logical limits.
/// Reset and Dispose invalidate access but cannot revoke references a caller already copied.
/// The store never disposes values and never calls host code while holding its lock.
/// </summary>
public sealed class VersionedSnapshotStore<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private long? _latestVersion, _lastPublishedVersion;
    private long _retainedItems, _itemHighWaterMark;
    private int _activeLeases, _queryLeases, _displayLeases, _pendingLeases;
    private int _versionHighWaterMark, _leaseHighWaterMark;
    private long _published, _rejected, _expired, _busyAcquisitions;
    private bool _stopped;

    public VersionedSnapshotStore(SnapshotBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Budget = budget;
    }

    public SnapshotBudget Budget { get; }
    public long? LatestVersion { get { lock (_gate) return _latestVersion; } }

    public SnapshotStoreStatistics Statistics
    {
        get
        {
            lock (_gate) return new(Budget, _entries.Count, _retainedItems, _activeLeases,
                _queryLeases, _displayLeases, _pendingLeases, _versionHighWaterMark,
                _itemHighWaterMark, _leaseHighWaterMark, _published, _rejected, _expired,
                _busyAcquisitions, _stopped);
        }
    }

    public SnapshotPublishStatus Publish(long version, T value, long itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        lock (_gate)
        {
            if (_stopped) return SnapshotPublishStatus.Stopped;
            if (_lastPublishedVersion is { } previous && version <= previous)
                throw new ArgumentOutOfRangeException(nameof(version), "Published versions must strictly increase, including across resets.");

            Entry? replaceable = null;
            if (_latestVersion is { } latest && _entries[latest].LeaseCount == 0)
                replaceable = _entries[latest];
            var retainedVersions = _entries.Count - (replaceable is null ? 0 : 1);
            var retainedItems = _retainedItems - (replaceable?.ItemCount ?? 0);
            // Subtract from the budget before adding: even a long.MaxValue item budget cannot overflow.
            if (retainedVersions >= Budget.MaximumVersions || itemCount > Budget.MaximumItems - retainedItems)
            {
                _rejected++;
                return SnapshotPublishStatus.BudgetExceeded;
            }

            _entries.Add(version, new Entry(version, value, itemCount));
            if (replaceable is not null) RemoveEntry(replaceable);
            _retainedItems += itemCount;
            _latestVersion = version;
            _lastPublishedVersion = version;
            _published++;
            _versionHighWaterMark = Math.Max(_versionHighWaterMark, _entries.Count);
            _itemHighWaterMark = Math.Max(_itemHighWaterMark, _retainedItems);
            return SnapshotPublishStatus.Published;
        }
    }

    public SnapshotAcquireResult<T> Acquire(long version, SnapshotOwner owner)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ValidateOwner(owner);
        lock (_gate)
        {
            if (_stopped) return new(SnapshotAcquireStatus.Stopped);
            if (!_entries.TryGetValue(version, out var entry) || entry.IsExpired)
            {
                _expired++;
                return new(SnapshotAcquireStatus.Expired);
            }
            if (_activeLeases >= Budget.MaximumLeases)
            {
                _busyAcquisitions++;
                return new(SnapshotAcquireStatus.Busy);
            }
            var lease = new SnapshotLease<T>(this, entry, owner);
            entry.LeaseCount++;
            _activeLeases++;
            AdjustOwner(owner, 1);
            _leaseHighWaterMark = Math.Max(_leaseHighWaterMark, _activeLeases);
            return new(SnapshotAcquireStatus.Acquired, lease);
        }
    }

    /// <summary>
    /// Retires all versions without stopping publication. Existing leases become unreadable, but
    /// their versions, items and owner slots remain charged until those leases are disposed.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_stopped) return;
            RetireEntries();
        }
    }

    /// <summary>Permanently stops publication and acquisition; outstanding leases still require disposal.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            RetireEntries();
        }
    }

    internal bool TryGetValue(SnapshotLease<T> lease, [NotNullWhen(true)] out T? value)
    {
        lock (_gate)
        {
            var entry = lease.Entry;
            if (entry is null || entry.IsExpired || _stopped)
            {
                value = null;
                return false;
            }
            value = entry.Value!;
            return true;
        }
    }

    internal bool Transfer(SnapshotLease<T> lease, SnapshotOwner owner)
    {
        ValidateOwner(owner);
        lock (_gate)
        {
            if (lease.Entry is not { IsExpired: false } || _stopped) return false;
            AdjustOwner(lease.Owner, -1);
            lease.SetOwner(owner);
            AdjustOwner(owner, 1);
            return true;
        }
    }

    internal void Release(SnapshotLease<T> lease)
    {
        lock (_gate)
        {
            var entry = lease.Entry;
            if (entry is null) return;
            lease.ClearEntry();
            entry.LeaseCount--;
            _activeLeases--;
            AdjustOwner(lease.Owner, -1);
            if (entry.LeaseCount == 0 && entry.Version != _latestVersion) RemoveEntry(entry);
        }
    }

    private void RetireEntries()
    {
        if (_latestVersion is { } latest && _entries[latest].LeaseCount == 0)
            RemoveEntry(_entries[latest]);
        _latestVersion = null;
        foreach (var entry in _entries.Values) entry.IsExpired = true;
    }

    private void RemoveEntry(Entry entry)
    {
        _entries.Remove(entry.Version);
        _retainedItems -= entry.ItemCount;
        entry.Value = null;
    }

    internal static void ValidateOwner(SnapshotOwner owner)
    {
        if (owner is not (SnapshotOwner.Query or SnapshotOwner.Display or SnapshotOwner.Pending))
            throw new ArgumentOutOfRangeException(nameof(owner));
    }

    private void AdjustOwner(SnapshotOwner owner, int adjustment)
    {
        switch (owner)
        {
            case SnapshotOwner.Query: _queryLeases += adjustment; break;
            case SnapshotOwner.Display: _displayLeases += adjustment; break;
            case SnapshotOwner.Pending: _pendingLeases += adjustment; break;
        }
    }

    internal sealed class Entry(long version, T value, long itemCount)
    {
        internal long Version { get; } = version;
        internal T? Value { get; set; } = value;
        internal long ItemCount { get; } = itemCount;
        internal int LeaseCount { get; set; }
        internal bool IsExpired { get; set; }
    }
}

/// <summary>
/// A scoped pin on a store version. Dispose is idempotent. TransferTo changes attribution in place,
/// without acquiring another slot. Never retain Value outside this lease's lifetime; Reset and
/// store disposal invalidate future access, including access through an otherwise undisposed lease.
/// </summary>
public sealed class SnapshotLease<T> : IDisposable where T : class
{
    private VersionedSnapshotStore<T>? _store;
    private int _owner;

    internal SnapshotLease(VersionedSnapshotStore<T> store, VersionedSnapshotStore<T>.Entry entry, SnapshotOwner owner)
    {
        _store = store;
        Entry = entry;
        Version = entry.Version;
        _owner = (int)owner;
    }

    internal VersionedSnapshotStore<T>.Entry? Entry { get; private set; }
    public long Version { get; }
    public SnapshotOwner Owner => (SnapshotOwner)Volatile.Read(ref _owner);

    public T Value
    {
        get
        {
            var store = Volatile.Read(ref _store);
            ObjectDisposedException.ThrowIf(store is null, this);
            return store.TryGetValue(this, out var value)
                ? value : throw new InvalidOperationException("The snapshot lease has expired or has been disposed.");
        }
    }

    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        var store = Volatile.Read(ref _store);
        if (store is not null) return store.TryGetValue(this, out value);
        value = null;
        return false;
    }

    public bool TransferTo(SnapshotOwner owner)
    {
        VersionedSnapshotStore<T>.ValidateOwner(owner);
        return Volatile.Read(ref _store)?.Transfer(this, owner) ?? false;
    }

    public void Dispose() => Volatile.Read(ref _store)?.Release(this);

    internal void SetOwner(SnapshotOwner owner) => Volatile.Write(ref _owner, (int)owner);

    internal void ClearEntry()
    {
        Entry = null;
        Volatile.Write(ref _store, null);
    }
}
