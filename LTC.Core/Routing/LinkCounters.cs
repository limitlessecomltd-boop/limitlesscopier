namespace LTC.Core.Routing;

/// <summary>
/// Rolling counters for a single copy link, since engine startup. The "today"
/// label in the UI is approximate — we don't roll over at midnight; users
/// typically restart the app daily so that's fine for now.
/// </summary>
public sealed record LinkCounters(
    int Total = 0,
    int Successful = 0,
    int Skipped = 0,
    int Failed = 0)
{
    public double SuccessRate =>
        Total == 0 ? 0 : (double)Successful / Total;
}

public sealed record LinkCountersSnapshot(Guid LinkId, LinkCounters Counters);
