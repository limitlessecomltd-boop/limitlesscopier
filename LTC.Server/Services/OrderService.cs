using System.Security.Cryptography;
using LTC.Server.Data;
using LTC.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace LTC.Server.Services;

/// <summary>
/// Wraps the order lifecycle so the endpoints don't deal with EF directly.
///
/// Lifecycle:
///   1. CreateAsync  - landing page calls /api/checkout/create
///                     -> we generate an OrderId, persist as "pending"
///   2. SetInvoiceAsync - after we call NowPayments, store the InvoiceId
///   3. ApplyWebhookAsync - NowPayments IPN arrives, we update status
///                          and when status hits "finished", issue+email
/// </summary>
public class OrderService
{
    private readonly LicensingDbContext _db;
    private readonly ILogger<OrderService> _log;

    public OrderService(LicensingDbContext db, ILogger<OrderService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Plan prices in USD. Source of truth - DO NOT trust the price
    /// from the customer's request. Read it from here based on the plan key.
    /// </summary>
    public static readonly Dictionary<string, decimal> PlanPrices = new()
    {
        ["1month"]  = 50m,
        ["3months"] = 120m,
        ["yearly"]  = 300m,
    };

    public static bool IsValidPlan(string plan) => PlanPrices.ContainsKey(plan);

    public static decimal GetPrice(string plan) => PlanPrices[plan];

    /// <summary>
    /// Create a new order in "pending" status. Returns the OrderId.
    /// </summary>
    public async Task<Order> CreateAsync(string email, string plan, CancellationToken ct = default)
    {
        if (!IsValidPlan(plan))
            throw new ArgumentException($"Unknown plan: {plan}", nameof(plan));

        var order = new Order
        {
            OrderId = GenerateOrderId(),
            Email = email.Trim(),
            Plan = plan,
            AmountUsd = GetPrice(plan),
            PayCurrency = "usdttrc20",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return order;
    }

    /// <summary>Store the NowPayments InvoiceId after invoice creation.</summary>
    public async Task SetInvoiceAsync(string orderId, string invoiceId, CancellationToken ct = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct).ConfigureAwait(false);
        if (order is null) return;
        order.InvoiceId = invoiceId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Order?> GetAsync(string orderId, CancellationToken ct = default)
    {
        return await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a NowPayments IPN webhook to an order.
    /// Idempotent on (orderId, paymentStatus) - calling twice with the same
    /// 'finished' payload won't issue two licenses.
    /// </summary>
    /// <returns>true if this call transitioned the order to "paid" for the first time</returns>
    public async Task<WebhookApplyResult> ApplyWebhookAsync(
        string orderId,
        string paymentId,
        string paymentStatus,
        string rawPayload,
        CancellationToken ct = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            _log.LogWarning("Webhook for unknown order_id={OrderId}", orderId);
            return WebhookApplyResult.UnknownOrder();
        }

        order.PaymentId = paymentId;
        order.LastWebhookAt = DateTime.UtcNow;
        order.LastWebhookPayload = rawPayload;

        // Map NowPayments status to our internal status.
        // NowPayments statuses: waiting, confirming, confirmed, sending, partially_paid,
        //                      finished, failed, refunded, expired
        var alreadyPaid = order.Status == "paid";

        switch (paymentStatus)
        {
            case "waiting":
                // Customer hasn't sent funds yet - leave as pending
                break;
            case "confirming":
            case "confirmed":
            case "sending":
                if (!alreadyPaid) order.Status = "confirming";
                break;
            case "finished":
                if (!alreadyPaid)
                {
                    order.Status = "paid";
                    order.PaidAt = DateTime.UtcNow;
                }
                break;
            case "partially_paid":
                if (!alreadyPaid) order.Status = "partial";
                break;
            case "failed":
                if (!alreadyPaid) order.Status = "failed";
                break;
            case "refunded":
                order.Status = "failed";   // treat refunds as terminal-failed for our purposes
                break;
            case "expired":
                if (!alreadyPaid) order.Status = "expired";
                break;
            default:
                _log.LogWarning("Unknown NowPayments status: {Status}", paymentStatus);
                break;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Only trigger license issuance ON the transition to paid, not on
        // re-receiving a 'finished' webhook for an already-paid order.
        var transitionedToPaid = order.Status == "paid" && !alreadyPaid;
        return new WebhookApplyResult
        {
            Order = order,
            TransitionedToPaid = transitionedToPaid,
            AlreadyHandled = alreadyPaid && paymentStatus == "finished",
        };
    }

    /// <summary>
    /// Mark an order as having had its license issued + email sent.
    /// Called by the webhook handler AFTER the email is dispatched.
    /// </summary>
    public async Task MarkLicenseIssuedAsync(string orderId, string licenseKey, CancellationToken ct = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct).ConfigureAwait(false);
        if (order is null) return;
        order.LicenseKey = licenseKey;
        order.LicenseIssuedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Generate "ord_" + 24 base32-ish random characters.</summary>
    private static string GenerateOrderId()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = RandomNumberGenerator.Create();
        var buf = new byte[24];
        rng.GetBytes(buf);
        var sb = new System.Text.StringBuilder("ord_", 28);
        foreach (var b in buf) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}

public class WebhookApplyResult
{
    public Order? Order { get; set; }
    public bool TransitionedToPaid { get; set; }
    public bool AlreadyHandled { get; set; }
    public bool IsUnknownOrder { get; set; }

    public static WebhookApplyResult UnknownOrder() => new() { IsUnknownOrder = true };
}
