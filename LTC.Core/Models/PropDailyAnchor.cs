using System;

namespace LTC.Core.Models;

/// <summary>
/// A snapshot of an account's equity at the moment its prop firm's daily
/// window rolled over. Persisted so that:
///
///   - The daily-loss meter has a stable reference point (not "live balance"
///     which would always read 0 because balance and the calc move together)
///   - App restarts don't lose the day's anchor
///   - We can backfill the high-water mark for trailing/HWM drawdown types
///
/// One row per (account, UTC date). The "date" is the date in UTC AT THE
/// MOMENT of the firm's reset hour, not local time. So if FTMO resets at
/// 00:00 CET (= 23:00 UTC previous day in summer), the anchor for the
/// trading session of Monday-CET is keyed to Sunday-UTC. The recorder is
/// responsible for that math; the model just stores what it's told.
///
/// Lifecycle:
///   1. Account is added or app starts → DailyAnchorRecorder checks: is
///      there an anchor for the current trading day? If not, snapshot
///      current equity NOW (with a flag so we know it was retro-anchored,
///      not a clean reset-hour snapshot).
///   2. At the firm's next reset hour, DailyAnchorRecorder writes a fresh
///      anchor with the equity reading from THAT moment.
///   3. UI reads the anchor for "today" to compute daily loss = anchor - equity.
/// </summary>
public sealed class PropDailyAnchor
{
    /// <summary>Login number — uniquely identifies the account on a broker.
    /// Same value as Account.Login.</summary>
    public ulong AccountLogin { get; set; }

    /// <summary>The trading date this anchor represents (UTC midnight of
    /// the date AS THE FIRM RECKONS IT). Stored as DateTime with TimeOfDay
    /// always = 00:00 — the time component is unused.</summary>
    public DateTime TradingDateUtc { get; set; }

    /// <summary>Equity reading at the moment of the firm's reset hour
    /// (or the moment of retro-anchor — see <see cref="RetroAnchored"/>).</summary>
    public decimal Equity { get; set; }

    /// <summary>Balance reading at the same moment. Stored alongside equity
    /// because some firms anchor on balance, not equity. We default to
    /// equity but keep both available.</summary>
    public decimal Balance { get; set; }

    /// <summary>Highest end-of-day equity observed since challenge start.
    /// Used by Trailing and HighWaterMark drawdown types. Updated daily
    /// by the recorder if the current day's CLOSING equity exceeds the
    /// previous mark. Persists across app restarts.</summary>
    public decimal HighWaterMark { get; set; }

    /// <summary>Wall-clock UTC time when the anchor row was created.
    /// Diagnostic — helps audit "did the recorder run at the right time?"</summary>
    public DateTime RecordedAtUtc { get; set; }

    /// <summary>True if this anchor was created after-the-fact (e.g. the
    /// app was off when the firm's reset hour passed). When true, the
    /// anchor's equity is a guess based on the first reading after the
    /// app came back online, NOT a clean reset-time snapshot. The Daily
    /// meter description in the UI shows a small "(estimated)" badge
    /// when reading from a retro-anchored record.</summary>
    public bool RetroAnchored { get; set; }
}
