namespace LTC.Core.Models;

/// <summary>
/// A single observable event in the copy pipeline — one row in the live activity tape.
/// </summary>
public sealed class ActivityEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public ActivityKind Kind { get; init; }
    public ActivityStatus Status { get; set; }

    public Guid? MasterAccountId { get; init; }
    public Guid? SlaveAccountId { get; init; }
    public string? MasterAccountLabel { get; init; }
    public string? SlaveAccountLabel { get; init; }

    public string? Symbol { get; init; }
    public string? OrderType { get; init; }
    public double Volume { get; init; }
    public double Price { get; init; }

    public ulong? MasterTicket { get; init; }
    public ulong? SlaveTicket { get; set; }

    /// <summary>
    /// Internal copier latency in microseconds — from receipt of master OnOrderUpdate
    /// to dispatch of slave OrderSendAsync. Sub-1ms is the v1 target.
    /// </summary>
    public long InternalLatencyMicros { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts so far (0 = first attempt).</summary>
    public int RetryCount { get; set; }
}

public enum ActivityKind
{
    Connect,
    Disconnect,
    SymbolsLoaded,
    Open,
    Close,
    Modify,
    Pending,
    PendingCancel,
    Filtered,
    Error
}

public enum ActivityStatus
{
    InFlight,
    Success,
    Failed,
    Skipped
}
