using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LTC.Core.Models;

namespace LTC.Core.Risk;

/// <summary>
/// Manages PropDailyAnchor records in memory for all prop firm accounts.
/// Called periodically (typically once per second by the engine's main
/// tick) with the latest equity reading. Updates anchors when:
///
///   1. There's no anchor yet for the current trading day → record a
///      retro-anchor with the current equity reading.
///
///   2. The trading day has rolled over (we crossed past the firm's
///      reset hour) → snapshot the current equity as the new anchor.
///
///   3. Current equity exceeds the high-water mark → update HWM.
///
/// Persistence is the caller's responsibility — this service holds the
/// in-memory state; a separate component (added in Tier 3) will persist
/// snapshots to SQLite at app close and reload at app start. For now,
/// anchors live in-memory only; an app restart causes a retro-anchor on
/// the next equity reading.
///
/// CONCURRENCY: methods are thread-safe via a single lock per account.
/// The engine ticks on its own thread; the UI reads anchors from the
/// dispatcher thread. We accept stale reads (off by ~1 second) in
/// exchange for not blocking the UI.
/// </summary>
public sealed class DailyAnchorRecorder
{
    private readonly ConcurrentDictionary<ulong, PropDailyAnchor> _anchors = new();
    private readonly object _gate = new();

    /// <summary>Optional persistence hooks. When set, the recorder calls
    /// <see cref="_persistSave"/> on every change to an anchor (snapshot
    /// to disk), and <see cref="LoadAll"/> can be called at startup to
    /// rehydrate from disk. Both delegates may be null — in that case
    /// the recorder is in-memory-only (test scenarios, etc).</summary>
    private readonly Action<PropDailyAnchor>? _persistSave;

    public DailyAnchorRecorder() : this(persistSave: null) { }

    public DailyAnchorRecorder(Action<PropDailyAnchor>? persistSave)
    {
        _persistSave = persistSave;
    }

    /// <summary>Rehydrate anchors from persistent storage at app start.
    /// Pass the anchors loaded from disk (e.g. via SettingsRepository).
    /// Anchors whose trading date is in the past will be detected as
    /// stale on the next Update() call and a fresh rollover anchor
    /// snapped — so calling this is always safe.</summary>
    public void LoadAll(IEnumerable<PropDailyAnchor> persisted)
    {
        foreach (var a in persisted)
        {
            if (a is null) continue;
            _anchors[a.AccountLogin] = a;
        }
    }

    /// <summary>Update the anchor for one account with a fresh equity reading.
    /// Returns the anchor that the UI should read RIGHT NOW for this account.</summary>
    /// <param name="login">Account login.</param>
    /// <param name="equity">Current equity from the broker.</param>
    /// <param name="balance">Current balance from the broker.</param>
    /// <param name="resetHourUtc">UTC time-of-day when the firm resets. Null = use 00:00 UTC.</param>
    public PropDailyAnchor Update(
        ulong login,
        decimal equity,
        decimal balance,
        TimeOnly? resetHourUtc)
    {
        var now = DateTime.UtcNow;
        var resetHour = resetHourUtc ?? new TimeOnly(0, 0);

        // Compute the trading-date this equity reading belongs to. The
        // trading day starts at the reset hour and lasts 24 hours. So:
        //   if now-time-of-day >= reset → today's date IS the trading date
        //   else → yesterday's date is still the trading date
        var nowTimeOfDay = TimeOnly.FromDateTime(now);
        DateTime tradingDate;
        if (nowTimeOfDay >= resetHour)
            tradingDate = now.Date;
        else
            tradingDate = now.Date.AddDays(-1);

        lock (_gate)
        {
            if (!_anchors.TryGetValue(login, out var existing))
            {
                // First-ever update for this account: retro-anchor.
                var fresh = new PropDailyAnchor
                {
                    AccountLogin    = login,
                    TradingDateUtc  = tradingDate,
                    Equity          = equity,
                    Balance         = balance,
                    HighWaterMark   = Math.Max(equity, balance),
                    RecordedAtUtc   = now,
                    RetroAnchored   = true,
                };
                _anchors[login] = fresh;
                try { _persistSave?.Invoke(fresh); } catch { /* don't crash poll on disk error */ }
                return fresh;
            }

            // Has the trading day rolled over since the last anchor?
            if (existing.TradingDateUtc < tradingDate)
            {
                // Yes — snapshot today's starting equity (NOT retro;
                // we observed the rollover live).
                var newDay = new PropDailyAnchor
                {
                    AccountLogin    = login,
                    TradingDateUtc  = tradingDate,
                    Equity          = equity,
                    Balance         = balance,
                    HighWaterMark   = Math.Max(existing.HighWaterMark,
                                               Math.Max(equity, balance)),
                    RecordedAtUtc   = now,
                    RetroAnchored   = false,
                };
                _anchors[login] = newDay;
                try { _persistSave?.Invoke(newDay); } catch { /* don't crash poll on disk error */ }
                return newDay;
            }

            // Same trading day. Maybe update the HWM if equity exceeded peak.
            if (equity > existing.HighWaterMark)
            {
                existing.HighWaterMark = equity;
            }
            return existing;
        }
    }

    /// <summary>Read the current anchor for an account without updating it.
    /// Returns null if no anchor has been recorded yet (e.g. account just
    /// added and no equity reading received).</summary>
    public PropDailyAnchor? Get(ulong login) =>
        _anchors.TryGetValue(login, out var a) ? a : null;

    /// <summary>For testing — clear all in-memory state.</summary>
    public void Clear() => _anchors.Clear();

    /// <summary>Diagnostic: how many accounts we're tracking.</summary>
    public int AnchorCount => _anchors.Count;
}
