namespace LTC.Core.Models;

/// <summary>
/// How the prop firm measures its max overall drawdown limit. The three
/// behaviours produce very different "how close am I to breach" math:
///
/// <see cref="StaticBalance"/>: drawdown is measured from the original
/// starting balance and never updates. The limit floor stays constant
/// regardless of profits. Used by The5%ers, Apex, Topstep funded.
///
/// <see cref="Trailing"/>: drawdown is measured from a rolling high of
/// the equity (or some firms: peak end-of-day balance). As the trader
/// makes profit, the limit "trails up" with them. Once the trailing
/// floor reaches a profit threshold it usually freezes (most firms cap
/// the trail at the initial balance + drawdown amount). Used by Apex
/// challenge and TFT challenge.
///
/// <see cref="HighWaterMark"/>: similar to trailing but more aggressive
/// — the floor lifts to lock in EVERY new equity high, never freezes.
/// Some smaller firms use this; usually a red flag for traders.
/// </summary>
public enum DrawdownType
{
    StaticBalance  = 0,
    Trailing       = 1,
    HighWaterMark  = 2,
}
