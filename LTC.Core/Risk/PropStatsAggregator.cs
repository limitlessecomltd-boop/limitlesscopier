using System;
using System.Collections.Generic;
using LTC.Core.Models;

namespace LTC.Core.Risk;

/// <summary>
/// Computes aggregate trade statistics for a prop firm account. The
/// inputs are the account config (challenge start date, reset hour) and
/// a stream of closed deals queried from MT5.
///
/// CURRENT IMPLEMENTATION STATUS:
///
///   The MT5 closed-deal accessor (IBrokerConnection.GetClosedDeals) does
///   not yet exist — adding it requires wiring through mtapi.mt5.dll and
///   handling the trade-history sync semantics correctly. For Tier 2,
///   this aggregator returns an empty PropTradeStats for every account.
///   The UI cards that depend on these stats (Today / Challenge Stats /
///   Consistency) gracefully degrade to "—" placeholders.
///
///   Once the IBrokerConnection.GetClosedDeals(since) method lands, the
///   <see cref="Aggregate"/> method below is the single integration point
///   — replace the TODO stub with a real implementation.
///
/// Design note: this is a static helper, not an instance-with-state, so
/// the UI can call it every tick without worrying about lifecycle. If we
/// later add caching (e.g. only re-query deal history every 30 seconds
/// instead of every tick), it'll happen in the calling ViewModel.
/// </summary>
public static class PropStatsAggregator
{
    /// <summary>Compute stats over the provided closed-deal list, scoped
    /// to the challenge window. Pure function.
    ///
    /// For Tier 2: callers will pass an empty list because the deal
    /// accessor isn't wired yet. The function correctly returns a
    /// zeroed-out PropTradeStats, which the UI handles.
    /// </summary>
    public static PropTradeStats Aggregate(
        IReadOnlyList<TradeRecord> closedDeals,
        DateTime? challengeStartUtc,
        TimeOnly? dailyResetUtc)
    {
        var stats = new PropTradeStats();
        if (closedDeals.Count == 0) return stats;

        var nowUtc = DateTime.UtcNow;
        var resetHour = dailyResetUtc ?? new TimeOnly(0, 0);
        var todayTradingDate = ComputeTradingDate(nowUtc, resetHour);
        var challengeStart = challengeStartUtc?.Date ?? DateTime.MinValue;

        var dailyMap = new Dictionary<DateTime, DailyPnlEntry>();

        foreach (var d in closedDeals)
        {
            // Scope filter — only deals on/after the challenge start date.
            if (d.ClosedAtUtc.Date < challengeStart) continue;

            // Per-deal trading-day bucket
            var dealTradingDate = ComputeTradingDate(d.ClosedAtUtc, resetHour);

            // Challenge-level aggregates
            stats.ChallengeTradeCount++;
            stats.ChallengeRealizedPnl += d.Profit;
            if (d.Profit >= 0)
            {
                stats.ChallengeWinCount++;
                stats.ChallengeGrossProfit += d.Profit;
                if (stats.ChallengeBestTrade is null
                    || d.Profit > stats.ChallengeBestTrade.Profit)
                {
                    stats.ChallengeBestTrade = d;
                }
            }
            else
            {
                stats.ChallengeLossCount++;
                stats.ChallengeGrossLoss += d.Profit;   // negative number
                if (stats.ChallengeWorstTrade is null
                    || d.Profit < stats.ChallengeWorstTrade.Profit)
                {
                    stats.ChallengeWorstTrade = d;
                }
            }

            // Today-level aggregates
            if (dealTradingDate == todayTradingDate)
            {
                stats.TodayTradeCount++;
                stats.TodayRealizedPnl += d.Profit;
                if (d.Profit >= 0)
                {
                    stats.TodayWinCount++;
                    if (stats.TodayBestTrade is null
                        || d.Profit > stats.TodayBestTrade.Profit)
                    {
                        stats.TodayBestTrade = d;
                    }
                }
                else
                {
                    stats.TodayLossCount++;
                    if (stats.TodayWorstTrade is null
                        || d.Profit < stats.TodayWorstTrade.Profit)
                    {
                        stats.TodayWorstTrade = d;
                    }
                }
            }

            // Per-day rollup for the consistency bar
            if (!dailyMap.TryGetValue(dealTradingDate, out var entry))
            {
                entry = new DailyPnlEntry { Date = dealTradingDate };
                dailyMap[dealTradingDate] = entry;
            }
            entry.RealizedPnl += d.Profit;
            entry.TradeCount++;
        }

        // Materialize daily history sorted oldest-first
        stats.DailyHistory = new List<DailyPnlEntry>(dailyMap.Values);
        stats.DailyHistory.Sort((a, b) => a.Date.CompareTo(b.Date));
        stats.ChallengeTradingDays = stats.DailyHistory.Count;

        // Best-day metric for the consistency rule.
        //
        // CONSISTENCY RULE SPEC (per the prop-firm convention the user
        // wants us to follow):
        //   - Numerator: the SINGLE biggest day's net realized P&L. A day's
        //     net P&L is the sum of every closed-trade P&L on that calendar
        //     date (reset-hour adjusted), losses subtracting from wins.
        //   - Denominator: total net realized P&L across the whole
        //     challenge so far. NOT gross profit.
        //   - Today's still-running day is EXCLUDED from the "biggest day"
        //     candidate set until the daily reset crosses — the rule
        //     locks in once a day is complete, and we shouldn't false-
        //     alarm mid-day on a big winner that the user might give back.
        foreach (var day in stats.DailyHistory)
        {
            if (day.Date == todayTradingDate) continue;  // today is not yet final
            if (day.RealizedPnl > stats.BestDayProfit)
            {
                stats.BestDayProfit = day.RealizedPnl;
                stats.BestDayDate   = day.Date;
            }
        }

        return stats;
    }

    /// <summary>Given a closed-deal list, the firm's starting balance, and the
    /// firm's daily reset hour, compute the equity-as-of-yesterday's-reset.
    /// This is the proper daily anchor for FTMO-style daily-loss enforcement:
    ///
    ///     daily_anchor = starting_balance + sum(realized P&amp;L of deals
    ///                                            closed BEFORE today's reset)
    ///
    /// Then the UI computes daily_loss = daily_anchor - current_equity.
    ///
    /// Returns null when the caller hasn't supplied a starting balance — the
    /// view model falls back to its current snapshot-based anchor in that case.
    /// </summary>
    public static decimal? ComputeDailyAnchorEquity(
        IReadOnlyList<TradeRecord> closedDeals,
        decimal startingBalance,
        TimeOnly? dailyResetUtc)
    {
        if (startingBalance <= 0) return null;

        var nowUtc = DateTime.UtcNow;
        var resetHour = dailyResetUtc ?? new TimeOnly(0, 0);

        // Compute today's reset DateTime in UTC.
        var nowTod = TimeOnly.FromDateTime(nowUtc);
        DateTime todayResetUtc = (nowTod >= resetHour)
            ? nowUtc.Date.Add(resetHour.ToTimeSpan())
            : nowUtc.Date.AddDays(-1).Add(resetHour.ToTimeSpan());

        decimal realizedBeforeReset = 0m;
        foreach (var d in closedDeals)
        {
            if (d.ClosedAtUtc < todayResetUtc)
            {
                realizedBeforeReset += d.Profit;
            }
        }
        return startingBalance + realizedBeforeReset;
    }

    /// <summary>Given a UTC timestamp and the firm's reset hour, return
    /// the date of the trading day that timestamp belongs to. Trading
    /// days run from reset hour T to reset hour T+24.</summary>
    private static DateTime ComputeTradingDate(DateTime utc, TimeOnly resetHour)
    {
        var tod = TimeOnly.FromDateTime(utc);
        return (tod >= resetHour) ? utc.Date : utc.Date.AddDays(-1);
    }
}
