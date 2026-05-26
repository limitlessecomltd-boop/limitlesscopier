using System;

namespace LTC.Server.Models;

/// <summary>
/// One row per checkout attempt. Created when the customer clicks
/// "Pay" on the landing page, updated as the payment progresses
/// (NowPayments IPN webhooks), terminal when license is emailed.
///
/// The PRIMARY KEY is our own generated OrderId, not NowPayments'
/// payment_id, because:
///   1. We need an id BEFORE we call NowPayments to put into ipn_callback_url
///   2. NowPayments doesn't always return a numeric payment_id reliably
///      on invoice creation - it materializes once the customer starts paying
///
/// Idempotency: webhook handler is keyed on OrderId. NowPayments retries
/// the IPN aggressively (every few minutes for hours), so we MUST guard
/// against issuing the same license twice. Approach: after a successful
/// 'finished' transition, set LicenseIssuedAt and never re-issue.
/// </summary>
public class Order
{
    /// <summary>Our generated order ID. Format: "ord_" + 24 random chars.</summary>
    public string OrderId { get; set; } = "";

    /// <summary>NowPayments' invoice_id (returned when we create the invoice).</summary>
    public string? InvoiceId { get; set; }

    /// <summary>NowPayments' payment_id (set once customer initiates payment).</summary>
    public string? PaymentId { get; set; }

    /// <summary>Customer's email - where the license will be delivered.</summary>
    public string Email { get; set; } = "";

    /// <summary>"1month" | "3months" | "yearly"</summary>
    public string Plan { get; set; } = "";

    /// <summary>
    /// Price quoted in USD. This is the FINAL price the customer pays,
    /// i.e. catalog price MINUS any discount. For affiliate codes (which
    /// give no discount) this equals the catalog price.
    /// </summary>
    public decimal AmountUsd { get; set; }

    /// <summary>Currency the customer pays in. Always "usdttrc20" for now.</summary>
    public string PayCurrency { get; set; } = "usdttrc20";

    /// <summary>
    /// Order lifecycle status:
    ///   "pending"   - created, customer hasn't paid yet
    ///   "confirming" - NowPayments detected the payment on-chain
    ///   "paid"       - payment confirmed (terminal success)
    ///   "expired"    - invoice expired without payment
    ///   "failed"     - payment failed for some reason
    ///   "partial"    - underpaid (manual review needed)
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>License key issued for this order. Null until status = "paid".</summary>
    public string? LicenseKey { get; set; }

    /// <summary>UTC timestamp when order was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp when license was issued + email sent.</summary>
    public DateTime? LicenseIssuedAt { get; set; }

    /// <summary>UTC timestamp when payment was confirmed by NowPayments.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>Last raw IPN payload, for audit + debugging.</summary>
    public string? LastWebhookPayload { get; set; }

    /// <summary>Last webhook received timestamp.</summary>
    public DateTime? LastWebhookAt { get; set; }

    // === ZIP 3: BEGIN - discount/affiliate code fields ===

    /// <summary>
    /// The literal code the customer typed at checkout (uppercased), or null
    /// if none. Could be a discount code OR an affiliate code. We resolve
    /// which it is at webhook-paid time to write the right redemption/commission row.
    /// Stored even for affiliate codes (which give no discount) so the webhook
    /// handler knows to credit a commission.
    /// </summary>
    public string? AppliedCode { get; set; }

    /// <summary>
    /// Discount applied to this order in USD. 0 for affiliate codes or no code.
    /// AmountUsd already reflects this subtraction; this field is kept for audit
    /// (so we can see "catalog was $120, discount $24, paid $96").
    /// </summary>
    public decimal DiscountAmountUsd { get; set; }

    // === ZIP 3: END ===
}
