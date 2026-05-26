namespace LTC.Server.Models;

/// <summary>
/// Customer initiates checkout: POST /api/checkout/create
/// </summary>
public class CreateCheckoutRequest
{
    /// <summary>"1month" | "3months" | "yearly"</summary>
    public string Plan { get; set; } = "";

    /// <summary>Customer email - where we'll deliver the license.</summary>
    public string Email { get; set; } = "";

    // === ZIP 3: BEGIN ===
    /// <summary>
    /// Optional discount or affiliate code. Null/empty = no code.
    /// Validated server-side; an invalid code is ignored (order proceeds
    /// at full price) rather than rejected, so a typo doesn't block a sale.
    /// </summary>
    public string? Code { get; set; }
    // === ZIP 3: END ===
}

/// <summary>
/// Response from POST /api/checkout/create.
/// Customer's browser uses InvoiceUrl to redirect to NowPayments hosted page.
/// </summary>
public class CreateCheckoutResponse
{
    public string OrderId { get; set; } = "";
    public string InvoiceUrl { get; set; } = "";
    public string Plan { get; set; } = "";

    /// <summary>Final price after discount (what the customer actually pays).</summary>
    public decimal AmountUsd { get; set; }

    // === ZIP 3: BEGIN ===
    /// <summary>The code that was applied (uppercased), or null if none/invalid.</summary>
    public string? AppliedCode { get; set; }

    /// <summary>Discount in USD (0 if affiliate code or no code).</summary>
    public decimal DiscountAmountUsd { get; set; }
    // === ZIP 3: END ===
}

/// <summary>
/// Response from GET /api/checkout/status/{orderId}.
/// Used by success.html to poll for payment confirmation.
/// </summary>
public class CheckoutStatusResponse
{
    /// <summary>"pending" | "confirming" | "paid" | "expired" | "failed" | "partial"</summary>
    public string Status { get; set; } = "";

    /// <summary>Present once status = "paid".</summary>
    public string? LicenseKey { get; set; }

    /// <summary>Customer-friendly message to display.</summary>
    public string Message { get; set; } = "";
}

// === ZIP 3: BEGIN - validate-code endpoint DTOs ===

/// <summary>
/// Request body for POST /api/checkout/validate-code.
/// Frontend calls this when the buyer enters a code, to show the
/// resulting price before they commit to paying.
/// </summary>
public class ValidateCodeRequest
{
    public string Code { get; set; } = "";
    public string Plan { get; set; } = "";
}

/// <summary>
/// Response from POST /api/checkout/validate-code.
/// </summary>
public class ValidateCodeResponse
{
    /// <summary>True if the code is valid and applicable to this plan/price.</summary>
    public bool Valid { get; set; }

    /// <summary>"discount" | "affiliate" | "none"</summary>
    public string Kind { get; set; } = "none";

    /// <summary>The catalog price for the plan, before any discount.</summary>
    public decimal OriginalPriceUsd { get; set; }

    /// <summary>Discount amount in USD (0 for affiliate codes).</summary>
    public decimal DiscountAmountUsd { get; set; }

    /// <summary>Final price after discount.</summary>
    public decimal FinalPriceUsd { get; set; }

    /// <summary>
    /// Customer-facing message. For valid discount codes: "20% off applied".
    /// For valid affiliate codes: "Referral code applied". For invalid: the reason.
    /// </summary>
    public string Message { get; set; } = "";
}

// === ZIP 3: END ===
