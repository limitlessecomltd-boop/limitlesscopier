using LTC.Core.Models;

namespace LTC.Core.Logging;

/// <summary>
/// Activity log abstraction — feeds the live tape in the UI and the forensic file log.
/// Implementations must be non-blocking on the hot path (Append never waits for I/O).
/// </summary>
public interface IActivityLog
{
    /// <summary>Append a new entry. Returns immediately; persistence happens on a worker thread.</summary>
    void Append(ActivityEntry entry);

    /// <summary>Update an in-flight entry once its outcome is known (e.g. slave ticket returned).</summary>
    void Update(Guid entryId, Action<ActivityEntry> mutate);

    /// <summary>Most recent N entries, newest first. For the UI tape.</summary>
    IReadOnlyList<ActivityEntry> Snapshot(int maxCount = 500);

    /// <summary>Raised whenever an entry is appended or updated. UI subscribes for live updates.</summary>
    event EventHandler<ActivityEntry>? EntryChanged;
}
