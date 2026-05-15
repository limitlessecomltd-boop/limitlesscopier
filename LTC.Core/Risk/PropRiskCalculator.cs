using LTC.Core.Models;

namespace LTC.Core.Risk;

/// <summary>
/// Computes how close a prop firm account is to its daily and overall
/// drawdown breach points. Pure function: takes a snapshot of the account's
/// current equity + the rule set + a daily-anchor equity value, returns a
/// <see cref="PropRiskState"/> describing the headroom in dollars and
/// percent.
///
/// The "daily anchor" is the account equity (or balance, per firm) at the
/// moment the firm's daily window rolled over — for FTMO that's 00:00
/// CE(S)T. Whoever calls this calculator is responsible for tracking that
/// anchor and persisting it across app restarts (so a midnight reboot
/// doesn't lose the "starting equity for today" reference). The
/// <c>PropDailyAnchor</c> persistence record (added in a later session)
/// handles that.
///
/// IMPORTANT: this is calculation only. It does NOT enforce. It does NOT
/// pause copies. Enforcement lives separately in the routing engine's
/// pre-trade check, which will call into this calculator to decide whether
/// a new trade can fit within remaining headroom.
/// </summary>
public static class PropRiskCalculator
{
    /// <summary>
    /// Compute the risk state for a prop account given its current equity
    /// snapshot and the equity it had at the start of today's window.
    /// </summary>
    /// <param name="config">The prop firm rule set attached to this account.</param>
    /// <param name="currentEquity">Live equity from the broker (balance + floating P&amp;L).
    /// Equity-based per the v1 spec — closed P&amp;L only would be balance.</param>
    /// <param name="dailyAnchorEquity">The equity recorded at the most recent
    /// daily reset (00:00 broker time for FTMO etc.). This is what the firm
    /// uses as the baseline for "loss today". For the very first day after
    /// account creation this equals the starting balance.</param>
    /// <param name="highWaterMarkEquity">For Trailing or HighWaterMark drawdown:
    /// the peak end-of-day equity observed (capped at starting balance + max
    /// loss amount for Trailing once that ceiling is hit). For StaticBalance
    /// drawdown: pass the StartingBalance value — the calculator ignores it
    /// in that mode.</param>
    public static PropRiskState Compute(
        PropFirmConfig config,
        decimal currentEquity,
        decimal dailyAnchorEquity,
        decimal highWaterMarkEquity)
    {
        // ---- DAILY ----
        // Floor concept mirrored from Overall, applied per-day:
        //   daily floor = dailyAnchorEquity - dailyLossLimit
        // Headroom = how far CURRENT equity sits above the daily floor.
        // This makes the meter behave correctly when you're up on the day:
        // if you've banked $300 today (closed) and have $100 floating profit,
        // your "you can lose today" reads as limit + $400, NOT just limit.
        //
        // Symmetry with Overall is the point: both meters now use
        //   "live equity - meter-specific floor"
        // as their headroom number, so closing a winning trade reduces
        // both meters' used-percent identically and same for closing a
        // losing trade — exactly what the user expects.
        decimal dailyFloor   = dailyAnchorEquity - config.DailyLossLimit;
        decimal dailyHeadroom = Math.Max(0m, currentEquity - dailyFloor);
        // "Loss used" stays clamped to 0 when you're up — never displays
        // negative loss. Used by the percent and by the breach detection.
        decimal dailyLossUsed = Math.Max(0m, dailyAnchorEquity - currentEquity);
        decimal dailyPercent = config.DailyLossLimit > 0
            ? (dailyLossUsed / config.DailyLossLimit) * 100m
            : 0m;

        // ---- OVERALL ----
        // The floor depends on the drawdown type:
        //   StaticBalance: floor = StartingBalance - MaxLossLimit (constant)
        //   Trailing/HWM:  floor = HighWaterMark - MaxLossLimit (moves up with equity)
        decimal floor = config.DrawdownType switch
        {
            DrawdownType.StaticBalance => config.StartingBalance - config.MaxLossLimit,
            DrawdownType.Trailing      => highWaterMarkEquity - config.MaxLossLimit,
            DrawdownType.HighWaterMark => highWaterMarkEquity - config.MaxLossLimit,
            _                          => config.StartingBalance - config.MaxLossLimit,
        };
        decimal overallLossUsed = Math.Max(0m, (config.StartingBalance) - currentEquity);
        // Headroom = how far above the floor we are right now
        decimal overallHeadroom = Math.Max(0m, currentEquity - floor);
        // Percent used = overall loss / max loss limit
        decimal overallPercent = config.MaxLossLimit > 0
            ? (overallLossUsed / config.MaxLossLimit) * 100m
            : 0m;

        // ---- CLOSEST BREACH ----
        // Whichever has less remaining headroom is the "active threat".
        // We expose this so the UI can highlight the right meter in red.
        bool dailyIsTighter = dailyHeadroom <= overallHeadroom;
        decimal closestHeadroom = dailyIsTighter ? dailyHeadroom : overallHeadroom;
        decimal closestPercent = dailyIsTighter ? dailyPercent : overallPercent;

        return new PropRiskState(
            DailyLossUsed:    dailyLossUsed,
            DailyHeadroom:    dailyHeadroom,
            DailyPercent:     dailyPercent,
            OverallLossUsed: overallLossUsed,
            OverallHeadroom: overallHeadroom,
            OverallPercent:  overallPercent,
            ClosestIsDaily:  dailyIsTighter,
            ClosestHeadroom: closestHeadroom,
            ClosestPercent:  closestPercent,
            Floor:           floor);
    }

    /// <summary>
    /// Would a trade with the given proposed loss-at-stop-loss fit inside
    /// remaining headroom? Used by the pre-trade safety check that we'll
    /// add to the routing engine in the auto-pause/auto-close session.
    ///
    /// proposedLossAtStop is the absolute dollar amount the slave would
    /// lose if its stop loss were hit. Calculated as
    /// (entry - SL) * pip value * volume on the calling side.
    /// </summary>
    public static bool TradeFitsInHeadroom(PropRiskState state, decimal proposedLossAtStop)
    {
        // Both daily and overall must accommodate the worst-case loss.
        return proposedLossAtStop <= state.DailyHeadroom
            && proposedLossAtStop <= state.OverallHeadroom;
    }
}

/// <summary>
/// Computed snapshot of a prop account's distance to breach. All amounts in
/// account currency. Percent values are 0-100+ (over 100 means breached).
/// </summary>
public sealed record PropRiskState(
    decimal DailyLossUsed,
    decimal DailyHeadroom,
    decimal DailyPercent,
    decimal OverallLossUsed,
    decimal OverallHeadroom,
    decimal OverallPercent,
    bool    ClosestIsDaily,
    decimal ClosestHeadroom,
    decimal ClosestPercent,
    decimal Floor);
