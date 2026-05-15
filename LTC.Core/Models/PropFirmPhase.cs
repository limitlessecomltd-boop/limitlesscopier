namespace LTC.Core.Models;

/// <summary>
/// Which phase of the prop firm program this account is in. Many firms have
/// different rules per phase — Phase 1 might require a higher profit target
/// than Phase 2, Funded accounts often drop the target entirely and relax
/// some restrictions.
///
/// We don't ship rules ourselves — the trader enters all their own numbers
/// from their firm's dashboard. This enum just lets them tag the account so
/// the UI can show "Phase 1 of 2" or "FUNDED" badges and the trader can
/// remember at a glance what stage they're in.
/// </summary>
public enum PropFirmPhase
{
    Phase1  = 0,
    Phase2  = 1,
    Funded  = 2,
}
