using System.Text.Json;
using LTC.Server.Endpoints;
using LTC.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LTC.Server.Endpoints;

/// <summary>
/// Receives Instant Payment Notifications (IPN) from NowPayments.
///
/// NowPayments retries this aggressively (every few minutes for several hours)
/// until we return 200. So:
///   1. We MUST be idempotent - duplicate webhooks for the same order_id
///      must not issue two licenses
///   2. We MUST return 200 for any webhook we successfully processed
///      (even if no state change happened - that's "successful no-op")
///   3. We return 4xx ONLY for malformed/unsigned bodies (NowPayments won't retry 4xx)
///
/// We deliberately DO NOT call this from MapAdminEndpoints because this is
/// public (no bearer token) - it's authenticated by HMAC signature verification.
/// </summary>
public static class NowPaymentsWebhookEndpoint
{
    public static void MapNowPaymentsWebhook(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/webhooks/nowpayments", HandleWebhook)
              .WithName("NowPaymentsWebhook");
    }

    private static async Task<IResult> HandleWebhook(
        HttpRequest request,
        NowPaymentsClient nowPay,
        OrderService orders,
        EmailService email,
        LicensingService licensing,
        CommissionService commissions,
        ILoggerFactory logFactory,
        CancellationToken ct)
    {
        var log = logFactory.CreateLogger("NowPaymentsWebhook");

        // 1. Read the raw body. We need the RAW bytes for signature verification -
        //    re-serialization by ASP.NET's model binding would change byte order.
        string rawBody;
        using (var reader = new StreamReader(request.Body))
        {
            rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        log.LogInformation("Webhook received ({Bytes} bytes)", rawBody.Length);

        // 2. Verify HMAC-SHA512 signature
        var sig = request.Headers["x-nowpayments-sig"].FirstOrDefault();
        if (!nowPay.VerifyIpnSignature(rawBody, sig))
        {
            log.LogWarning("IPN signature verification FAILED, rejecting");
            return Results.Unauthorized();
        }
        log.LogInformation("IPN signature verified");

        // 3. Parse the body
        string? orderId;
        string? paymentStatus;
        string? paymentId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            orderId       = TryGetString(root, "order_id");
            paymentStatus = TryGetString(root, "payment_status");
            paymentId     = TryGetString(root, "payment_id");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to parse webhook body");
            return Results.BadRequest();
        }

        if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(paymentStatus))
        {
            log.LogWarning("Webhook missing order_id or payment_status");
            return Results.BadRequest();
        }

        // 4. Apply to our state machine
        var result = await orders.ApplyWebhookAsync(
            orderId, paymentId ?? "", paymentStatus, rawBody, ct).ConfigureAwait(false);

        if (result.IsUnknownOrder)
        {
            // Could be a test webhook or a hand-crafted call. Return 200 so
            // NowPayments doesn't retry, but log it.
            log.LogWarning("Webhook for unknown order_id={OrderId} - acking and dropping", orderId);
            return Results.Ok();
        }

        if (result.AlreadyHandled)
        {
            log.LogInformation("Webhook duplicate (order {OrderId} already paid+issued) - acking", orderId);
            return Results.Ok();
        }

        if (!result.TransitionedToPaid)
        {
            // Status moved but didn't reach paid. Just ack.
            log.LogInformation("Order {OrderId} status={Status}", orderId, result.Order!.Status);
            return Results.Ok();
        }

        // 5. Transition to paid -> issue license + email it
        var order = result.Order!;
        log.LogInformation("Issuing license for paid order {OrderId} email={Email} plan={Plan}",
            order.OrderId, order.Email, order.Plan);

        string licenseKey;
        try
        {
            // Plan -> days mapping. "yearly" still gets a fixed-duration license
            // (365 days) rather than perpetual, so we can clear it if needed.
            int? days = order.Plan switch
            {
                "1month"   => 30,
                "3months"  => 90,
                "yearly"   => 365,
                _ => null
            };
            licenseKey = await licensing.IssueKeyAsync(
                email: order.Email,
                plan: order.Plan,
                days: days,
                notes: $"NowPayments order {order.OrderId} ${order.AmountUsd}",
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "License issuance failed for order {OrderId}", order.OrderId);
            // Return 5xx so NowPayments retries - we want eventual issuance.
            return Results.Problem(statusCode: 500);
        }

        // 6. Send email. We persist the license key BEFORE sending email so that
        //    if email fails, we don't keep re-minting on retries - we just retry
        //    the email.
        await orders.MarkLicenseIssuedAsync(order.OrderId, licenseKey, ct).ConfigureAwait(false);

        // === ZIP 3: record code redemption + affiliate commission (if any).
        // Wrapped so a bookkeeping failure never blocks license delivery — the
        // license is already minted and that's what the customer paid for.
        // CommissionService is idempotent, so a webhook retry re-runs it safely.
        try
        {
            await commissions.RecordForPaidOrderAsync(order, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Commission/redemption recording failed for order {OrderId} " +
                             "(license still delivered; reconcile manually)", order.OrderId);
        }
        // === ZIP 3: END ===

        var sent = await email.SendLicenseEmailAsync(
            order.Email, licenseKey, order.Plan, order.AmountUsd, ct).ConfigureAwait(false);
        if (!sent)
        {
            log.LogError("Email failed for order {OrderId} - license {Key} was minted but not delivered. " +
                         "Manual intervention required (resend via AdminApp or copy from DB).",
                         order.OrderId, licenseKey);
            // Still return 200 - the license IS issued, the email is a separate concern.
            // Manual resend can recover this without re-triggering NowPayments retries.
        }

        return Results.Ok();
    }

    private static string? TryGetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }
}
