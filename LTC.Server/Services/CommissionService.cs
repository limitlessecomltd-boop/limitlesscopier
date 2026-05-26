using LTC.Server.Data;
using LTC.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace LTC.Server.Services;

/// <summary>
/// Records the consequences of a code being used on a PAID order.
/// Called by the NowPayments webhook handler at the moment an order
/// transitions to "paid", after the license is issued.
///
/// Why at paid-time and not checkout-time:
///   If we incremented UsedCount or wrote commissions when the invoice was
///   created, an abandoned/unpaid checkout would burn a code's usage and
///   credit phantom commissions. Only a real payment should count.
///
/// Idempotency:
///   The webhook can fire multiple times for the same order (NowPayments
///   retries). We guard with:
///     - Commissions has a UNIQUE index on OrderId (a second insert throws)
///     - We check CodeRedemptions for an existing row for this OrderId
///   So calling this twice for the same order is a safe no-op the second time.
/// </summary>
public class CommissionService
{
    private readonly LicensingDbContext _db;
    private readonly AffiliateService _aff;
    private readonly ILogger<CommissionService> _log;

    // Fixed commission rate for affiliate referrals.
    private const decimal CommissionRate = 0.20m;   // 20%

    // Hold period before a commission becomes "earned" (matches refund window).
    private static readonly TimeSpan HoldPeriod = TimeSpan.FromDays(14);

    public CommissionService(LicensingDbContext db, AffiliateService aff, ILogger<CommissionService> log)
    {
        _db = db;
        _aff = aff;
        _log = log;
    }

    /// <summary>
    /// Process a paid order's applied code. Safe to call multiple times.
    /// </summary>
    public async Task RecordForPaidOrderAsync(Order order, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(order.AppliedCode))
            return;   // no code on this order, nothing to record

        // Idempotency guard: if we already wrote a redemption for this order, stop.
        var alreadyRecorded = await _db.CodeRedemptions
            .AnyAsync(r => r.OrderId == order.OrderId, ct).ConfigureAwait(false);
        if (alreadyRecorded)
        {
            _log.LogInformation("Code redemption already recorded for order {OrderId} - skipping",
                order.OrderId);
            return;
        }

        var lookup = await _aff.ResolveCodeAsync(order.AppliedCode, ct).ConfigureAwait(false);

        switch (lookup.Kind)
        {
            case AffiliateService.CodeKind.Discount:
            {
                var d = lookup.Discount!;
                _db.CodeRedemptions.Add(new CodeRedemption
                {
                    OrderId = order.OrderId,
                    DiscountCodeId = d.Id,
                    AffiliateId = null,
                    CodeString = order.AppliedCode,
                    DiscountAmountUsd = order.DiscountAmountUsd,
                    RedeemedAt = DateTime.UtcNow,
                });
                d.UsedCount += 1;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _log.LogInformation("Discount code {Code} redeemed on order {OrderId} (used {Used}x)",
                    order.AppliedCode, order.OrderId, d.UsedCount);
                break;
            }

            case AffiliateService.CodeKind.Affiliate:
            {
                var a = lookup.Affiliate!;

                // Defensive self-referral guard at record-time too (in case the
                // order slipped through with the affiliate's own email).
                if (a.License is not null &&
                    string.Equals(a.License.Email?.Trim(), order.Email?.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("Self-referral detected at paid-time for order {OrderId} - not crediting",
                        order.OrderId);
                    // Still record the redemption for audit, but no commission.
                    _db.CodeRedemptions.Add(new CodeRedemption
                    {
                        OrderId = order.OrderId,
                        AffiliateId = a.Id,
                        CodeString = order.AppliedCode,
                        DiscountAmountUsd = 0m,
                        RedeemedAt = DateTime.UtcNow,
                    });
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return;
                }

                var now = DateTime.UtcNow;
                var commissionAmount = Math.Round(order.AmountUsd * CommissionRate, 2);

                _db.CodeRedemptions.Add(new CodeRedemption
                {
                    OrderId = order.OrderId,
                    AffiliateId = a.Id,
                    CodeString = order.AppliedCode,
                    DiscountAmountUsd = 0m,
                    RedeemedAt = now,
                });

                _db.Commissions.Add(new Commission
                {
                    AffiliateId = a.Id,
                    OrderId = order.OrderId,
                    Plan = order.Plan,
                    OrderAmountUsd = order.AmountUsd,
                    CommissionAmountUsd = commissionAmount,
                    Status = "pending",
                    PaidAt = order.PaidAt ?? now,
                    EligibleAt = (order.PaidAt ?? now).Add(HoldPeriod),
                });

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Affiliate commission created: code {Code} order {OrderId} amount ${Amt} (eligible {Eligible:u})",
                    order.AppliedCode, order.OrderId, commissionAmount,
                    (order.PaidAt ?? now).Add(HoldPeriod));
                break;
            }

            default:
                _log.LogWarning("Order {OrderId} had code {Code} but it no longer resolves - ignoring",
                    order.OrderId, order.AppliedCode);
                break;
        }
    }
}
