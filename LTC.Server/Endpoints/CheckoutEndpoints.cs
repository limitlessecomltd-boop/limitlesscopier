using System.Text.RegularExpressions;
using LTC.Server.Models;
using LTC.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LTC.Server.Endpoints;

/// <summary>
/// Customer-facing checkout endpoints.
///
/// /api/checkout/create        - landing page calls this with {plan, email},
///                               we create an order + NowPayments invoice,
///                               return the invoice_url for browser redirect
/// /api/checkout/status/{id}   - success.html polls this to learn when the
///                               webhook has confirmed payment + issued license
/// </summary>
public static class CheckoutEndpoints
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Use IEndpointRouteBuilder so this works whether called on
    // `app` directly or on a RouteGroupBuilder (e.g. one with rate limiting attached).
    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/checkout/create", CreateCheckout)
              .WithName("CreateCheckout");

        routes.MapGet("/api/checkout/status/{orderId}", GetStatus)
              .WithName("GetCheckoutStatus");

        // === ZIP 3 ===
        routes.MapPost("/api/checkout/validate-code", ValidateCode)
              .WithName("ValidateCheckoutCode");
    }

    // === ZIP 3: BEGIN ===
    private static async Task<IResult> ValidateCode(
        [FromBody] ValidateCodeRequest req,
        CheckoutValidationService validator,
        CancellationToken ct)
    {
        if (!OrderService.IsValidPlan(req.Plan))
            return Results.BadRequest(new { error = "Unknown plan" });

        var r = await validator.ValidateAsync(req.Code, req.Plan, buyerEmail: null, ct)
                               .ConfigureAwait(false);

        return Results.Ok(new ValidateCodeResponse
        {
            Valid = r.Valid,
            Kind = r.Kind,
            OriginalPriceUsd = r.OriginalPriceUsd,
            DiscountAmountUsd = r.DiscountAmountUsd,
            FinalPriceUsd = r.FinalPriceUsd,
            Message = r.Message,
        });
    }
    // === ZIP 3: END ===

    private static async Task<IResult> CreateCheckout(
        [FromBody] CreateCheckoutRequest req,
        OrderService orders,
        NowPaymentsClient nowPay,
        CheckoutValidationService validator,
        IConfiguration config,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !EmailRegex.IsMatch(req.Email))
            return Results.BadRequest(new { error = "Valid email required" });

        if (!OrderService.IsValidPlan(req.Plan))
            return Results.BadRequest(new { error = "Unknown plan" });

        // === ZIP 3: validate code server-side. Invalid codes don't block the
        // sale — validator returns "none"/full-price in that case, EXCEPT for
        // self-referral which we surface as an error so the affiliate notices.
        var validation = await validator.ValidateAsync(req.Code, req.Plan, req.Email, ct)
                                        .ConfigureAwait(false);
        // The only validation failure we hard-reject is self-referral; everything
        // else just falls back to full price (NormalizedCode == null).
        if (!validation.Valid &&
            validation.Message.Contains("your own", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = validation.Message });
        }

        // 1. Persist the order (we need the id BEFORE creating the invoice
        //    so we can stamp it as ipn_callback_url metadata)
        var order = await orders.CreateAsync(
            req.Email, req.Plan,
            finalPriceUsd: validation.FinalPriceUsd,
            appliedCode: validation.NormalizedCode,
            discountAmountUsd: validation.DiscountAmountUsd,
            ct: ct).ConfigureAwait(false);

        // 2. Resolve the URLs that NowPayments needs to know about
        var apiBase = config["Public:ApiBaseUrl"]
                      ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                      ?? "https://api.limitlesscopier.com";
        var landingBase = config["Public:LandingBaseUrl"]
                          ?? Environment.GetEnvironmentVariable("LANDING_BASE_URL")
                          ?? "https://limitlesscopier.com";

        var ipnUrl     = $"{apiBase.TrimEnd('/')}/webhooks/nowpayments";
        var successUrl = $"{landingBase.TrimEnd('/')}/success.html?order_id={order.OrderId}";
        var cancelUrl  = $"{landingBase.TrimEnd('/')}/checkout.html?cancelled=1";

        // 3. Call NowPayments to create the invoice
        var planLabel = req.Plan switch
        {
            "1month"   => "1 Month",
            "3months"  => "3 Months",
            "yearly"   => "Yearly",
            _ => req.Plan
        };

        CreateInvoiceResult invoice;
        try
        {
            invoice = await nowPay.CreateInvoiceAsync(
                orderId: order.OrderId,
                amountUsd: order.AmountUsd,
                customerEmail: order.Email,
                orderDescription: $"Limitless Trade Copier - {planLabel}",
                payCurrency: "usdttrc20",
                ipnCallbackUrl: ipnUrl,
                successUrl: successUrl,
                cancelUrl: cancelUrl,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // We have a 'pending' order with no invoice - acceptable, it'll
            // just sit and expire. Customer sees an error and can retry.
            return Results.Problem(
                title: "Could not create payment invoice",
                detail: ex.Message,
                statusCode: 502);
        }

        // 4. Store the InvoiceId on our order
        await orders.SetInvoiceAsync(order.OrderId, invoice.InvoiceId, ct).ConfigureAwait(false);

        // 5. Return the invoice URL - browser redirects here
        return Results.Ok(new CreateCheckoutResponse
        {
            OrderId = order.OrderId,
            InvoiceUrl = invoice.InvoiceUrl,
            Plan = order.Plan,
            AmountUsd = order.AmountUsd,
            AppliedCode = order.AppliedCode,
            DiscountAmountUsd = order.DiscountAmountUsd,
        });
    }

    private static async Task<IResult> GetStatus(
        string orderId,
        OrderService orders,
        CancellationToken ct)
    {
        var order = await orders.GetAsync(orderId, ct).ConfigureAwait(false);
        if (order is null) return Results.NotFound();

        var (message, includeKey) = order.Status switch
        {
            "pending"    => ("Waiting for payment...", false),
            "confirming" => ("Payment detected, confirming on the blockchain...", false),
            "paid"       => ("Payment confirmed - check your email for the license key.", true),
            "expired"    => ("This payment session expired. Please start a new checkout.", false),
            "failed"     => ("The payment failed. Please try again or contact support.", false),
            "partial"    => ("Underpayment detected. Contact support@limitlesscopier.com.", false),
            _            => ("Unknown status.", false)
        };

        return Results.Ok(new CheckoutStatusResponse
        {
            Status = order.Status,
            LicenseKey = includeKey ? order.LicenseKey : null,
            Message = message,
        });
    }
}
