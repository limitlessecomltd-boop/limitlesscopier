namespace LTC.Core.Models;

/// <summary>
/// The prop firm rule set attached to a single account. Only relevant when
/// the parent <see cref="Account.Kind"/> is <see cref="AccountKind.PropChallenge"/>
/// or <see cref="AccountKind.PropFunded"/>; null for retail accounts.
///
/// IMPORTANT: every value in here comes from the trader, not from us. We do
/// NOT ship a preset library and we do NOT verify that the numbers match
/// what the firm's website says. The trader is responsible for entering the
/// correct values from their own firm's dashboard. Our role is to do the
/// arithmetic ("you're at X% of the daily limit") accurately against
/// whatever numbers they gave us.
///
/// Why no presets:
///   - Prop firms change their rules without notice; a stale preset would
///     be worse than no preset.
///   - If we shipped a preset and a customer breached because our preset was
///     wrong, we'd carry partial responsibility for the loss.
///   - "Works with ANY prop firm" is a stronger product position than
///     "supports the 7 firms we hand-coded." Long-tail firms are a real
///     segment of our target audience.
///   - It forces the trader to read their firm's dashboard during setup —
///     useful discipline.
///
/// All monetary fields are absolute dollar amounts in the account's currency.
/// The setup UI offers a quick-fill button next to each limit ("use 5% of
/// balance" → computes and inserts the dollar amount) but those are generic
/// percentages, not firm-specific presets. The dollar amount is what gets
/// stored.
/// </summary>
public sealed class PropFirmConfig
{
    /// <summary>Free-text name of the firm or program — whatever the trader
    /// wants to call it. Examples: "FTMO 100k Challenge", "MyFundedFX",
    /// "Personal hedge fund Phase 2", "My buddy's prop deal". We display it
    /// in the UI and never parse it.</summary>
    public string FirmName { get; set; } = string.Empty;

    /// <summary>Current phase. Affects which rules apply (or don't) for
    /// most firms — e.g. Funded usually has no profit target. Trader-set;
    /// the app uses it only for display and for hiding/showing the
    /// "profit target" field in the UI.</summary>
    public PropFirmPhase Phase { get; set; } = PropFirmPhase.Phase1;

    /// <summary>Starting balance the firm credited the account with. Used
    /// as the baseline for the trailing-drawdown high-water-mark and for
    /// the percentage-helper buttons in the setup UI. Trader-entered.</summary>
    public decimal StartingBalance { get; set; }

    /// <summary>Maximum loss in a single trading day, absolute dollars.
    /// Trader enters this from their firm's dashboard. The UI offers a
    /// generic "5% of balance" helper button but doesn't force any value.</summary>
    public decimal DailyLossLimit { get; set; }

    /// <summary>Maximum overall account loss, absolute dollars.
    /// Trader-entered. UI helper: "10% of balance" generic button.</summary>
    public decimal MaxLossLimit { get; set; }

    /// <summary>How the firm measures overall drawdown. Static = from
    /// starting balance forever. Trailing = follows equity up but freezes.
    /// HighWaterMark = follows equity up forever. Trader picks from a
    /// radio group with explanatory text next to each option.</summary>
    public DrawdownType DrawdownType { get; set; } = DrawdownType.StaticBalance;

    /// <summary>Time-of-day (UTC) when the firm's daily window rolls over.
    /// Null = auto-detect from the broker's server clock (recommended;
    /// works correctly for FTMO and most others because their MT5 servers
    /// run on the firm's timezone). Trader can override if they know their
    /// firm uses a different reset time.</summary>
    public TimeOnly? DailyResetUtc { get; set; }

    /// <summary>Optional profit target to pass the challenge, absolute
    /// dollars. Trader enters from their firm's dashboard. Hidden on
    /// Funded phase since most firms drop the target there.</summary>
    public decimal? ProfitTarget { get; set; }

    /// <summary>Optional minimum trading days required by the firm.
    /// Trader-entered. Null when their firm doesn't have one.</summary>
    public int? MinTradingDays { get; set; }

    /// <summary>Optional maximum challenge period in days (the deadline
    /// to hit the profit target). Most firms have one — e.g. FTMO 30 days,
    /// FundedNext 60 days, The5%ers unlimited (= null here). Trader-entered.
    /// Combined with <see cref="ChallengeStartDateUtc"/> the UI computes
    /// "you have X days left" and warns when running out.</summary>
    public int? MaxChallengePeriodDays { get; set; }

    /// <summary>UTC date the trader started this challenge — typically the
    /// day the firm credited the account. Used together with
    /// <see cref="MaxChallengePeriodDays"/> to compute days remaining.
    /// Defaults to "now" when the account is first added; trader can
    /// adjust if backdating.</summary>
    public DateTime? ChallengeStartDateUtc { get; set; }

    /// <summary>Optional consistency rule: the firm forbids any single day's
    /// profit from being more than this percent of total profit. FTMO uses
    /// 40, FundedNext uses 50, The5%ers doesn't have one (null). Trader
    /// enters from their firm's dashboard. UI shows a warning if the
    /// trader's best day approaches this threshold.</summary>
    public int? ConsistencyMaxDayPercent { get; set; }

    /// <summary>Optional max-loss-per-trade rule, expressed as percent of
    /// starting balance. Some prop firms (MyForexFunds variants, certain
    /// futures-style firms) cap how much you can lose on ANY single
    /// position. Example: 2% of $100k = max $2,000 floating loss per
    /// open trade. The Prop Journal tab surfaces a meter for the worst
    /// currently-open trade vs this limit. Null = no per-trade limit
    /// configured.</summary>
    public decimal? MaxLossPerTradePercent { get; set; }

    /// <summary>If set true, automatically close every open position on
    /// this account the moment realized + floating P&amp;L reaches the
    /// configured ProfitTarget. The intent is to lock in the win on
    /// challenge accounts where flickering back below target before close
    /// would reset progress. Has no effect when ProfitTarget is null or
    /// zero. One-shot per app session — once triggered, won't fire again
    /// until the app restarts (to avoid loops if positions re-open).
    /// Default false (opt-in).</summary>
    public bool CloseAllOnTargetHit { get; set; }

    // -----------------------------------------------------------------
    // Opt-in safety automations. Both default to null (= disabled) so a
    // first-time prop user has to deliberately enable them per account.
    // -----------------------------------------------------------------

    /// <summary>If set (1-99), automatically stop copying NEW trades onto
    /// this account once daily loss reaches this percent of the daily limit.
    /// Existing positions stay open. The user is warned in the activity log.
    /// Null = disabled.</summary>
    public int? AutoPauseAtPercent { get; set; }

    /// <summary>If set (1-99), automatically CLOSE all open positions on
    /// this account once daily loss reaches this percent. This is the
    /// "save my account" nuclear option. Should be higher than AutoPause
    /// (e.g. pause at 80%, close at 95%) so the trader has a chance to
    /// react manually first. Null = disabled.</summary>
    public int? AutoCloseAtPercent { get; set; }
}
