using System.Diagnostics;

namespace LTC.Core.Diagnostics;

/// <summary>
/// High-resolution timing helpers built on <see cref="Stopwatch"/>.
/// All methods are allocation-free and safe to call on the hot path.
/// </summary>
public static class LatencyClock
{
    /// <summary>Frequency of the underlying timer (ticks per second).</summary>
    public static readonly long Frequency = Stopwatch.Frequency;

    private static readonly double TicksPerMicrosecond = Frequency / 1_000_000.0;
    private static readonly double TicksPerMillisecond = Frequency / 1_000.0;

    /// <summary>Get the current high-resolution timestamp (use as a delta source).</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Convert a delta of timestamps into microseconds.</summary>
    public static long DeltaMicros(long startTimestamp, long endTimestamp)
        => (long)((endTimestamp - startTimestamp) / TicksPerMicrosecond);

    /// <summary>Convert a delta of timestamps into milliseconds (double precision).</summary>
    public static double DeltaMillis(long startTimestamp, long endTimestamp)
        => (endTimestamp - startTimestamp) / TicksPerMillisecond;

    /// <summary>Microseconds elapsed since the given timestamp.</summary>
    public static long ElapsedMicros(long sinceTimestamp)
        => DeltaMicros(sinceTimestamp, Now());
}
