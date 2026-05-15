namespace LTC.Core.Logging;

/// <summary>
/// In-memory ring buffer of plain-English log lines, intended for the UI's Logs view.
/// Distinct from the file logger (which has full technical detail) — this buffer
/// holds only user-facing messages: connections, trade copies, errors that the
/// user can act on. Engine internals like dispatcher races stay out.
/// </summary>
public sealed class PlainLogBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<PlainLogEntry> _entries = new();
    private readonly int _capacity;

    public PlainLogBuffer(int capacity = 2000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Fires whenever a new entry is appended. UI marshals to dispatcher thread.</summary>
    public event EventHandler<PlainLogEntry>? EntryAdded;

    /// <summary>Append a new entry. Thread-safe.</summary>
    public void Append(PlainLogLevel level, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var entry = new PlainLogEntry(DateTime.UtcNow, level, message);
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > _capacity) _entries.RemoveFirst();
        }
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>Snapshot of the entire buffer in chronological order.</summary>
    public IReadOnlyList<PlainLogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>Drop everything. Used by the "Clear logs" button in the UI.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}

public sealed record PlainLogEntry(DateTime TimestampUtc, PlainLogLevel Level, string Message);

public enum PlainLogLevel
{
    Info,
    Warning,
    Error,
}
