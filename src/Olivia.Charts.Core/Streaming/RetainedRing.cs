using System.Collections.Immutable;

namespace Olivia.Core.Streaming;

public readonly record struct RetentionStatistics(int Capacity, int RetainedItems, long AppendedItems, long EvictedItems, long ReleasedItems = 0);

/// <summary>
/// Explicit count-based raw retention, separate from input admission and display coalescing.
/// Appends evict the oldest retained values; snapshots are copied and oldest-first.
/// Item values must be immutable. This is in-memory retention, not durable persistence.
/// </summary>
public sealed class RetainedRing<T>
{
    private readonly object _gate = new();
    private readonly T[] _items;
    private int _start, _count;
    private long _appended, _released;

    public RetainedRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public RetentionStatistics Statistics
    {
        get { lock (_gate) return new(_items.Length, _count, _appended, _appended - _count - _released, _released); }
    }

    public void Append(ReadOnlySpan<T> items)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = item;
                    _count++;
                }
                else
                {
                    _items[_start] = item;
                    _start = (_start + 1) % _items.Length;
                }
                _appended++;
            }
        }
    }

    /// <summary>Releases current retention. Explicit release is counted separately from rolling eviction.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _released += _count;
            Array.Clear(_items);
            _start = _count = 0;
        }
    }

    public ImmutableArray<T> Snapshot()
    {
        lock (_gate)
        {
            var result = ImmutableArray.CreateBuilder<T>(_count);
            for (var index = 0; index < _count; index++) result.Add(_items[(_start + index) % _items.Length]);
            return result.MoveToImmutable();
        }
    }
}
