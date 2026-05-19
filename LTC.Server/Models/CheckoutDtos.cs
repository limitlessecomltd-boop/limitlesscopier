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
    public decimal AmountUsd { get; set; }
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
