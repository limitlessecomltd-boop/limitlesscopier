using LTC.Server.Models;
using LTC.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LTC.Server.Services;

/// <summary>
/// Validates a discount or affiliate code at checkout time and computes
/// the resulting price. Shared by:
///   - POST /api/checkout/validate-code  (preview, before paying)
///   - POST /api/checkout/create         (the actual order)
///
/// Design choice — invalid codes don't block the sale:
///   When validating during order creation, an invalid/expired/maxed code
///   is treated as "no code" rather than an error. A buyer who fat-fingers
///   a code shouldn't be unable to buy; they just pay full price. The
///   validate-code endpoint surfaces the problem in the UI beforehand so
///   this is rarely a surprise.
///
/// Self-referral note:
///   The spec forbids an affiliate using their own code. But at checkout we
///   only have the buyer's email, not a logged-in identity. We approximate
///   the rule by rejecting the code if the affiliate's own license email
///   matches the checkout email. Not bulletproof (different emails dodge it)
///   but catches the obvious case. Real enforcement would need accounts.
/// </summary>
public class CheckoutValidationService
{
    private readonly AffiliateService _aff;
    private readonly LicensingDbContext _db;
    private readonly ILogger<CheckoutValidationService> _log;

    public CheckoutValidationService(AffiliateService aff, LicensingDbContext db, ILogger<CheckoutValidationService> log)
    {
        _aff = aff;
        _db = db;
        _log = log;
    }

    public sealed record ValidationResult(
        bool Valid,
        string Kind,                  // "discount" | "affiliate" | "none"
        decimal OriginalPriceUsd,
        decimal DiscountAmountUsd,
        decimal FinalPriceUsd,
        string Message,
        string? NormalizedCode);      // uppercased code to store, or null

    /// <summary>
    /// Validate a code for a plan. <paramref name="buyerEmail"/> is optional;
    /// when provided, used for the self-referral check on affiliate codes.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        string? code,
        string plan,
        string? buyerEmail = null,
        CancellationToken ct = default)
    {
        if (!OrderService.IsValidPlan(plan))
            return new ValidationResult(false, "none", 0, 0, 0, "Unknown plan", null);

        var catalog = OrderService.GetPrice(plan);

        // No code -> valid "none", full price.
        if (string.IsNullOrWhiteSpace(code))
            return new ValidationResult(true, "none", catalog, 0, catalog, "", null);

        var normalized = code.Trim().ToUpperInvariant();
        var lookup = await _aff.ResolveCodeAsync(normalized, ct).ConfigureAwait(false);

        switch (lookup.Kind)
        {
            case AffiliateService.CodeKind.Discount:
            {
                var d = lookup.Discount!;

                if (!d.Enabled)
                    return Fail(catalog, "This code is no longer active.");

                if (d.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
                    return Fail(catalog, "This code has expired.");

                if (d.MaxUses is int max && d.UsedCount >= max)
                    return Fail(catalog, "This code has reached its usage limit.");

                if (d.MinPurchaseUsd is decimal min && catalog < min)
                    return Fail(catalog, $"This code requires a minimum purchase of ${min:0.##}.");

                // Compute discount
                decimal discount;
                if (d.DiscountPercent > 0)
                    discount = Math.Round(catalog * d.DiscountPercent / 100m, 2);
                else
                    discount = d.DiscountFlatUsd;

                // Clamp: never discount below $1 (NowPayments min) and never negative.
                if (discount > catalog - 1m) discount = catalog - 1m;
                if (discount < 0m) discount = 0m;

                var final = catalog - discount;
                var msg = d.DiscountPercent > 0
                    ? $"{d.DiscountPercent}% off applied"
                    : $"${d.DiscountFlatUsd:0.##} off applied";

                return new ValidationResult(true, "discount", catalog, discount, final, msg, normalized);
            }

            case AffiliateService.CodeKind.Affiliate:
            {
                var a = lookup.Affiliate!;

                // Self-referral check (best-effort by email).
                if (!string.IsNullOrWhiteSpace(buyerEmail) &&
                    a.License is not null &&
                    string.Equals(a.License.Email?.Trim(), buyerEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(catalog, "You can't use your own referral code.");
                }

                // Affiliate codes now ALSO grant the buyer a discount, taken
                // from the globally-linked discount code (admin-configurable via
                // the AppSetting "AffiliateDiscountCodeId"). If no code is linked,
                // affiliate codes give no discount (legacy behavior). The buyer
                // pays the discounted price; the affiliate's commission is later
                // computed on that discounted AmountUsd (in CommissionService).
                var linked = await GetLinkedAffiliateDiscountAsync(ct).ConfigureAwait(false);
                if (linked is null)
                {
                    return new ValidationResult(true, "affiliate", catalog, 0, catalog,
                        "Referral code applied", normalized);
                }

                // Compute the linked discount against the catalog price.
                decimal affDiscount;
                if (linked.DiscountPercent > 0)
                    affDiscount = Math.Round(catalog * linked.DiscountPercent / 100m, 2);
                else
                    affDiscount = linked.DiscountFlatUsd;

                if (affDiscount > catalog - 1m) affDiscount = catalog - 1m;   // keep >= $1
                if (affDiscount < 0m) affDiscount = 0m;

                var affFinal = catalog - affDiscount;
                var affMsg = linked.DiscountPercent > 0
                    ? $"Referral code applied — {linked.DiscountPercent}% off"
                    : $"Referral code applied — ${linked.DiscountFlatUsd:0.##} off";

                return new ValidationResult(true, "affiliate", catalog, affDiscount, affFinal,
                    affMsg, normalized);
            }

            default:
                return Fail(catalog, "That code isn't valid.");
        }
    }

    /// <summary>
    /// Returns the DiscountCode linked as the global "affiliate discount" via
    /// the AppSetting "AffiliateDiscountCodeId", or null if none is set, the
    /// setting is empty, the code no longer exists, or the code is disabled.
    /// </summary>
    private async Task<DiscountCode?> GetLinkedAffiliateDiscountAsync(CancellationToken ct)
    {
        var setting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "AffiliateDiscountCodeId", ct)
            .ConfigureAwait(false);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
            return null;
        if (!Guid.TryParse(setting.Value, out var id))
            return null;
        var dc = await _db.DiscountCodes.FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (dc is null || !dc.Enabled) return null;
        return dc;
    }

    private static ValidationResult Fail(decimal catalog, string message) =>
        new(false, "none", catalog, 0, catalog, message, null);
}
