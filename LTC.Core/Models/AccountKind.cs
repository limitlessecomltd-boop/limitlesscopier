namespace LTC.Core.Models;

/// <summary>
/// What kind of trading account this is. Drives the onboarding flow, the
/// presence of prop-firm risk meters in the UI, and (later) any per-account
/// safety automations like auto-pause on daily loss threshold.
///
/// <see cref="Personal"/> is the retail trader — their own money, no firm
/// rules, no daily drawdown enforcement. They might still want soft caps
/// (a "stop copying after -$200 today" feature) but those are opt-in.
///
/// <see cref="PropChallenge"/> is the trader currently taking a prop firm
/// evaluation (FTMO Phase 1/2, FundedNext challenge, etc). The firm
/// enforces strict daily and overall drawdown limits — if breached, the
/// challenge ends and the trader loses the evaluation fee. Risk meters
/// are the headline feature for this user.
///
/// <see cref="PropFunded"/> is the same trader after passing the
/// evaluation — they're now trading the firm's real capital with profit
/// share. Same drawdown rules typically apply (often slightly relaxed),
/// but the stakes are higher: blowing a funded account means losing the
/// account permanently, not just a $300 evaluation fee.
/// </summary>
public enum AccountKind
{
    Personal     = 0,
    PropChallenge = 1,
    PropFunded   = 2,
}
