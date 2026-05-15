using LTC.Core.Models;

namespace LTC.Core.Logging;

/// <summary>
/// In-memory ring-buffer activity log. Append/Update never block on I/O — UI subscribes
/// via <see cref="EntryChanged"/> for live updates, and the (optional) sink callback gets
/// a copy on a background thread for forensic logging to file.
/// </summary>
public sealed class ActivityLog : IActivityLog
{
    private readonly object _lock = new();
    private readonly ActivityEntry?[] _ring;   // indexed by (head + i) % capacity
    private int _head = 0;
    private int _count = 0;

    /// <summary>Optional asynchronous sink (e.g. Serilog file write).</summary>
    public Action<ActivityEntry>? Sink { get; set; }

    public ActivityLog(int capacity = 10_000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new ActivityEntry?[capacity];
    }

    public event EventHandler<ActivityEntry>? EntryChanged;

    public void Append(ActivityEntry entry)
    {
        lock (_lock)
        {
            int idx = (_head + _count) % _ring.Length;
            if (_count == _ring.Length)
            {
                // Buffer full: overwrite the oldest, advance head.
                _ring[_head] = entry;
                _head = (_head + 1) % _ring.Length;
            }
            else
            {
                _ring[idx] = entry;
                _count++;
            }
        }
        SafeRaise(entry);
        SafeSink(entry);
    }

    public void Update(Guid entryId, Action<ActivityEntry> mutate)
    {
        ActivityEntry? found = null;
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _ring.Length;
                var e = _ring[idx];
                if (e is not null && e.Id == entryId)
                {
                    found = e;
                    break;
                }
            }
        }
        if (found is null) return;
        try { mutate(found); } catch { /* tolerate */ }
        SafeRaise(found);
        SafeSink(found);
    }

    public IReadOnlyList<ActivityEntry> Snapshot(int maxCount = 500)
    {
        lock (_lock)
        {
            int take = Math.Min(maxCount, _count);
            var list = new List<ActivityEntry>(take);
            // Newest first: walk back from the tail.
            for (int i = 0; i < take; i++)
            {
                int idx = (_head + _count - 1 - i + _ring.Length) % _ring.Length;
                var e = _ring[idx];
                if (e is not null) list.Add(e);
            }
            return list;
        }
    }

    private void SafeRaise(ActivityEntry e)
    {
        // Fire on a thread-pool task to keep callers off the UI thread and avoid
        // re-entrancy from event handlers back into Append (which would deadlock the lock).
        var handler = EntryChanged;
        if (handler is null) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { handler(this, e); }
            catch { /* a single handler exception must not kill the next */ }
        }, null);
    }

    private void SafeSink(ActivityEntry e)
    {
        var sink = Sink;
        if (sink is null) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { sink(e); } catch { /* never propagate */ }
        }, null);
    }
}
