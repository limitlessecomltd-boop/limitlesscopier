using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LTC.Server.Models;

/// <summary>
/// One affiliate record per License. Every paying customer automatically
/// gets an affiliate record when their license is issued, but the customer-
/// facing <see cref="Code"/> slug stays NULL until they explicitly claim it
/// from the dashboard.
///
/// Why one-per-license rather than one-per-email: a customer might buy
/// multiple licenses with the same email (e.g. for a team). Each license
/// is a separate referral capacity. Keeps the math obvious.
///
/// Self-referral guard: <see cref="CheckoutValidationService"/> rejects
/// a code that resolves to the same License the buyer would be paying for.
/// Today this is enforced by email match because we don't have user accounts.
/// </summary>
public class Affiliate
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to the License that owns this affiliate slot.</summary>
    public Guid LicenseId { get; set; }
    [ForeignKey(nameof(LicenseId))]
    public License License { get; set; } = null!;

    /// <summary>
    /// Customer-chosen short slug, e.g. "noman". Null until claimed from
    /// the dashboard. Once set, it's LOCKED — changing it would break
    /// outstanding referrals. Stored uppercase for case-insensitive lookup.
    /// Min 3 chars, max 24 chars, ASCII letters/digits only (enforced
    /// at the API layer, not at the schema).
    /// </summary>
    [MaxLength(24)]
    public string? Code { get; set; }

    /// <summary>UTC timestamp when Code was set (null if never claimed).</summary>
    public DateTime? CodeClaimedAt { get; set; }

    /// <summary>
    /// Lifetime total of commissions earned (sum of Commission.AmountUsd
    /// where Status='earned' or 'paid'). Denormalized so the dashboard
    /// can render without an aggregate query.
    /// </summary>
    public decimal TotalEarnedUsd { get; set; }

    /// <summary>
    /// Lifetime total of commissions paid out (sum of Commission.AmountUsd
    /// where Status='paid'). Denormalized.
    /// </summary>
    public decimal TotalPaidUsd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Admin-created discount code. Two flavors:
///   - Percent: <see cref="DiscountPercent"/> set (e.g. 20 = 20% off)
///   - Flat:    <see cref="DiscountFlatUsd"/> set (e.g. 10 = $10 off)
///
/// Exactly ONE of those two must be > 0; the other is 0.
/// Validation enforces this; the schema does not.
/// </summary>
public class DiscountCode
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The customer-facing code, e.g. "LAUNCH20". Stored uppercase.</summary>
    [Required, MaxLength(32)]
    public string Code { get; set; } = "";

    /// <summary>Percentage off (0-100). 0 means "use flat instead".</summary>
    public int DiscountPercent { get; set; }

    /// <summary>Flat dollar discount. 0 means "use percent instead".</summary>
    public decimal DiscountFlatUsd { get; set; }

    /// <summary>
    /// Maximum total redemptions allowed. Null = unlimited.
    /// Race: two concurrent checkouts both incrementing UsedCount could
    /// over-redeem by 1 in theory. Acceptable for v1 (Stripe makes you
    /// design around this too).
    /// </summary>
    public int? MaxUses { get; set; }

    /// <summary>How many times this code has been redeemed.</summary>
    public int UsedCount { get; set; }

    /// <summary>UTC expiry. Null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Minimum order amount in USD for the code to apply. Null/0 = no minimum.
    /// </summary>
    public decimal? MinPurchaseUsd { get; set; }

    /// <summary>Soft-disable toggle. False codes always reject.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Free-form admin label for ops (e.g. "Q2 launch promo").</summary>
    [MaxLength(256)]
    public string? Notes { get; set; }
}

/// <summary>
/// Audit row recording ONE redemption of EITHER a discount code OR an
/// affiliate code on ONE order. Created when the order transitions to
/// "paid" (NOT at checkout-create time — that would let unpaid orders
/// inflate UsedCount and lock other customers out of legitimate codes).
///
/// Exactly one of <see cref="DiscountCodeId"/>/<see cref="AffiliateId"/>
/// is set per row (codes are mutually exclusive at checkout).
/// </summary>
public class CodeRedemption
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to the Order that was paid for.</summary>
    [Required, MaxLength(40)]
    public string OrderId { get; set; } = "";

    /// <summary>FK to a DiscountCode (set if the code was a discount code).</summary>
    public Guid? DiscountCodeId { get; set; }

    /// <summary>FK to an Affiliate (set if the code was an affiliate code).</summary>
    public Guid? AffiliateId { get; set; }

    /// <summary>The literal code string the customer typed. Stored for audit.</summary>
    [Required, MaxLength(32)]
    public string CodeString { get; set; } = "";

    /// <summary>Discount amount applied to the order (USD). 0 for affiliate codes.</summary>
    public decimal DiscountAmountUsd { get; set; }

    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One commission row per affiliate-attributed sale.
///
/// Status lifecycle:
///   "pending"   - sale just happened, within refund window
///   "earned"    - 14 days after PaidAt, no refund happened, money is owed
///   "cancelled" - the original order was refunded/disputed within 14 days
///   "paid"      - admin sent the affiliate their money and marked it paid
///
/// EligibleAt = PaidAt + 14 days. A background job (or a lazy on-read
/// check in the dashboard endpoint) flips status from "pending" to "earned"
/// once EligibleAt passes and the underlying order is still in good standing.
/// </summary>
public class Commission
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The affiliate who earns this.</summary>
    public Guid AffiliateId { get; set; }
    [ForeignKey(nameof(AffiliateId))]
    public Affiliate Affiliate { get; set; } = null!;

    /// <summary>The order this commission is for.</summary>
    [Required, MaxLength(40)]
    public string OrderId { get; set; } = "";

    /// <summary>Plan that was sold (for the dashboard's "you referred X" UI).</summary>
    [Required, MaxLength(20)]
    public string Plan { get; set; } = "";

    /// <summary>Order total (USD) AFTER any discount. Commission is calculated from this.</summary>
    public decimal OrderAmountUsd { get; set; }

    /// <summary>Commission amount in USD (20% of OrderAmountUsd, frozen at sale time).</summary>
    public decimal CommissionAmountUsd { get; set; }

    /// <summary>"pending" | "earned" | "cancelled" | "paid"</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "pending";

    /// <summary>When the original order was paid.</summary>
    public DateTime PaidAt { get; set; }

    /// <summary>PaidAt + 14 days. After this, status -> "earned" (unless cancelled).</summary>
    public DateTime EligibleAt { get; set; }

    /// <summary>When status flipped to "earned" (null while pending).</summary>
    public DateTime? EarnedAt { get; set; }

    /// <summary>When admin paid this out (null until paid).</summary>
    public DateTime? PaidOutAt { get; set; }

    /// <summary>When status flipped to "cancelled" (null unless cancelled).</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Optional admin note ("paid via USDT 0xabc...", "refunded order").</summary>
    [MaxLength(256)]
    public string? Notes { get; set; }
}
