using System;
using System.Collections.Generic;

namespace LTC.Core.Models;

/// <summary>
/// Aggregated statistics about an account's closed-trade history.
/// Populated by a periodic poll of MT5 closed deals and consumed by
/// the Prop Journal tab cards (Today / Challenge Stats / Consistency).
///
/// One instance covers ONE account. The producer is responsible for
/// scoping deals appropriately (e.g. since the challenge start date)
/// so the trader doesn't see stats polluted by trades from before
/// they started the prop challenge.
///
/// All money values are in the account's deposit currency.
/// </summary>
public sealed class PropTradeStats
{
    // ------------------------------------------------------------------
    // TODAY — since the firm's daily reset hour
    // ------------------------------------------------------------------

    public int TodayTradeCount { get; set; }
    public int TodayWinCount { get; set; }
    public int TodayLossCount { get; set; }
    public decimal TodayRealizedPnl { get; set; }

    /// <summary>Largest single winning trade closed today. Null if no trades.</summary>
    public TradeRecord? TodayBestTrade { get; set; }

    /// <summary>Largest single losing trade closed today. Null if no trades.</summary>
    public TradeRecord? TodayWorstTrade { get; set; }

    public double TodayWinRate =>
        TodayTradeCount > 0 ? (double)TodayWinCount / TodayTradeCount * 100.0 : 0.0;

    // ------------------------------------------------------------------
    // CHALLENGE — since the challenge start date
    // ------------------------------------------------------------------

    public int ChallengeTradeCount { get; set; }
    public int ChallengeWinCount { get; set; }
    public int ChallengeLossCount { get; set; }
    public decimal ChallengeRealizedPnl { get; set; }
    public decimal ChallengeGrossProfit { get; set; }
    public decimal ChallengeGrossLoss { get; set; }

    public TradeRecord? ChallengeBestTrade { get; set; }
    public TradeRecord? ChallengeWorstTrade { get; set; }

    public double ChallengeWinRate =>
        ChallengeTradeCount > 0 ? (double)ChallengeWinCount / ChallengeTradeCount * 100.0 : 0.0;

    /// <summary>Profit factor: gross profit ÷ |gross loss|. >1.0 means
    /// winning, <1.0 means losing. Returns 0 if no losses (avoid div/0).</summary>
    public decimal ChallengeProfitFactor =>
        ChallengeGrossLoss != 0
            ? Math.Abs(ChallengeGrossProfit / ChallengeGrossLoss)
            : (ChallengeGrossProfit > 0 ? 999m : 0m);

    public decimal ChallengeAverageWin =>
        ChallengeWinCount > 0 ? ChallengeGrossProfit / ChallengeWinCount : 0m;

    public decimal ChallengeAverageLoss =>
        ChallengeLossCount > 0 ? ChallengeGrossLoss / ChallengeLossCount : 0m;

    /// <summary>How many distinct UTC dates have at least one closed trade
    /// since the challenge started. Used by the Trading Days meter to
    /// compute progress toward the firm's minimum.</summary>
    public int ChallengeTradingDays { get; set; }

    // ------------------------------------------------------------------
    // CONSISTENCY — daily P&L breakdown for the consistency rule meter
    // ------------------------------------------------------------------

    /// <summary>One entry per distinct trading day, oldest first.
    /// Empty when no trades yet. Used by the consistency bar chart.</summary>
    public List<DailyPnlEntry> DailyHistory { get; set; } = new();

    /// <summary>Largest single-day profit observed during the challenge.
    /// Used by the consistency rule warning ("your best day is $840,
    /// that's 24% of total — within 40% rule").</summary>
    public decimal BestDayProfit { get; set; }

    /// <summary>The UTC date of the best day. Display only.</summary>
    public DateTime? BestDayDate { get; set; }

    /// <summary>Net realized profit used as the consistency-rule denominator.
    /// This is the sum of every closed deal's signed P&L since challenge
    /// start (wins minus losses) — same as ChallengeRealizedPnl. Matches
    /// how FTMO/FundedNext/MFF compute "total profit" for the consistency
    /// check: it's what's actually in your equity above starting balance,
    /// not gross winning trades.
    /// Returns 0 if the trader is at break-even or net negative — the rule
    /// only kicks in once there's positive net profit on the table.</summary>
    public decimal TotalGrossProfit =>
        ChallengeRealizedPnl > 0 ? ChallengeRealizedPnl : 0m;

    /// <summary>Best day as percent of total net realized profit. The
    /// trader's consistency rule warns when this approaches the firm's
    /// threshold (e.g. FTMO 40%, FundedNext 50%). Returns 0 if no profit
    /// yet. Today's incomplete day is excluded from the BestDayProfit
    /// computation (see PropStatsAggregator).</summary>
    public decimal BestDayPercentOfTotal =>
        TotalGrossProfit > 0
            ? (BestDayProfit / TotalGrossProfit) * 100m
            : 0m;
}

/// <summary>A single closed trade for "best trade" / "worst trade" displays.</summary>
public sealed class TradeRecord
{
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";  // "BUY" / "SELL"
    public double Volume { get; set; }
    public decimal Profit { get; set; }
    public DateTime ClosedAtUtc { get; set; }
}

/// <summary>Per-day P&L summary for the consistency bar chart.</summary>
public sealed class DailyPnlEntry
{
    public DateTime Date { get; set; }              // UTC midnight of the day
    public decimal RealizedPnl { get; set; }        // sum of closed deals that day
    public int TradeCount { get; set; }
}
