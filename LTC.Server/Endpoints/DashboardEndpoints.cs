using LTC.Server.Data;
using LTC.Server.Models;
using LTC.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LTC.Server.Endpoints;

/// <summary>
/// Customer-facing dashboard endpoints. Auth = knowledge-of-license-key.
/// Anyone with the license key can view + manage that license. Rate-
/// limited via the existing "checkout" policy (10/min/IP) so the API
/// can't be brute-forced for valid keys.
///
/// Endpoints:
///   GET  /api/dashboard/{licenseKey}             - render the dashboard
///   POST /api/dashboard/{licenseKey}/claim-code  - claim an affiliate slug
///   POST /api/dashboard/{licenseKey}/resend-license - re-fire license email
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet ("/api/dashboard/{licenseKey}",                  GetDashboard)        .WithName("GetDashboard");
        routes.MapPost("/api/dashboard/{licenseKey}/claim-code",       ClaimAffiliateCode)  .WithName("ClaimAffiliateCode");
        routes.MapPost("/api/dashboard/{licenseKey}/resend-license",   ResendLicenseEmail)  .WithName("ResendLicenseEmail");
        return routes;
    }

    // =========================================================
    // GET /api/dashboard/{licenseKey}
    // =========================================================

    private static async Task<IResult> GetDashboard(
        string licenseKey,
        LicensingDbContext db,
        AffiliateService aff,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return Results.BadRequest(new { error = "license key required" });

        var key = licenseKey.Trim().ToUpperInvariant();

        var lic = await db.Licenses
            .Include(l => l.Activation)
            .FirstOrDefaultAsync(l => l.LicenseKey == key, ct).ConfigureAwait(false);

        if (lic is null) return Results.NotFound(new { error = "license not found" });

        // Find (or lazily create) the affiliate row. Legacy licenses issued
        // before this build won't have one; we create on-the-fly.
        var affiliate = await db.Affiliates
            .FirstOrDefaultAsync(a => a.LicenseId == lic.Id, ct).ConfigureAwait(false);
        if (affiliate is null)
        {
            affiliate = new Affiliate
            {
                LicenseId = lic.Id,
                CreatedAt = DateTime.UtcNow,
            };
            db.Affiliates.Add(affiliate);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Lazy eligibility flip: any "pending" commissions past their hold
        // get flipped to "earned" before we render. Idempotent and cheap.
        await aff.FlipPendingCommissionsForAffiliateAsync(affiliate.Id, ct).ConfigureAwait(false);

        // Reload affiliate after the flip so denormalized totals are fresh.
        affiliate = await db.Affiliates.FirstAsync(a => a.Id == affiliate.Id, ct).ConfigureAwait(false);

        // Pull recent commissions for the table on the dashboard.
        var recentCommissions = await db.Commissions
            .Where(c => c.AffiliateId == affiliate.Id)
            .OrderByDescending(c => c.PaidAt)
            .Take(20)
            .Select(c => new
            {
                c.Plan,
                c.OrderAmountUsd,
                c.CommissionAmountUsd,
                c.Status,
                c.PaidAt,
                c.EligibleAt,
                c.EarnedAt,
                c.PaidOutAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        // Pending balance = sum of earned but not yet paid.
        var pendingBalance = await db.Commissions
            .Where(c => c.AffiliateId == affiliate.Id && c.Status == "earned")
            .SumAsync(c => (decimal?)c.CommissionAmountUsd, ct).ConfigureAwait(false) ?? 0m;

        // Count referrals = how many sales used this code, regardless of status.
        var totalReferrals = await db.Commissions
            .CountAsync(c => c.AffiliateId == affiliate.Id, ct).ConfigureAwait(false);

        // Days remaining on this license. Null for lifetime.
        int? daysRemaining = null;
        if (lic.ExpiresAt is DateTime exp)
        {
            var remaining = (int)Math.Ceiling((exp - DateTime.UtcNow).TotalDays);
            daysRemaining = remaining > 0 ? remaining : 0;
        }

        return Results.Ok(new
        {
            license = new
            {
                key = lic.LicenseKey,
                email = MaskEmail(lic.Email),
                plan = lic.Plan,
                expiresAt = lic.ExpiresAt,
                daysRemaining,
                createdAt = lic.CreatedAt,
                revoked = lic.Revoked,
                status = lic.Revoked ? "revoked"
                       : (lic.ExpiresAt is DateTime e && e < DateTime.UtcNow) ? "expired"
                       : "active",
            },
            activation = lic.Activation == null ? null : new
            {
                machineShort = lic.Activation.MachineShort,
                firstSeenAt = lic.Activation.FirstSeenAt,
                lastHeartbeatAt = lic.Activation.LastHeartbeatAt,
            },
            affiliate = new
            {
                code = affiliate.Code?.ToLowerInvariant(),
                codeClaimedAt = affiliate.CodeClaimedAt,
                commissionRatePercent = 20,
                totalEarnedUsd = affiliate.TotalEarnedUsd,
                totalPaidUsd = affiliate.TotalPaidUsd,
                pendingBalanceUsd = pendingBalance,
                totalReferrals,
                payoutThresholdUsd = 50m,
                payoutInstructions = "Email admin@limitlesscopier.com from your registered address when your earned balance reaches $50. We pay via USDT TRC20 or bank transfer."
            },
            recentReferrals = recentCommissions,
        });
    }

    // =========================================================
    // POST /api/dashboard/{licenseKey}/claim-code
    // =========================================================

    public sealed class ClaimCodeRequest
    {
        public string Code { get; set; } = "";
    }

    private static async Task<IResult> ClaimAffiliateCode(
        string licenseKey,
        [FromBody] ClaimCodeRequest req,
        AffiliateService aff,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return Results.BadRequest(new { error = "license key required" });

        var key = licenseKey.Trim().ToUpperInvariant();
        var result = await aff.ClaimSlugAsync(key, req.Code ?? "", ct).ConfigureAwait(false);

        if (!result.Ok)
            return Results.BadRequest(new { error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            code = result.Affiliate!.Code!.ToLowerInvariant(),
            claimedAt = result.Affiliate.CodeClaimedAt,
        });
    }

    // =========================================================
    // POST /api/dashboard/{licenseKey}/resend-license
    // =========================================================

    private static async Task<IResult> ResendLicenseEmail(
        string licenseKey,
        LicensingDbContext db,
        EmailService email,
        ILoggerFactory logFactory,
        CancellationToken ct)
    {
        var log = logFactory.CreateLogger("ResendLicenseEndpoint");
        if (string.IsNullOrWhiteSpace(licenseKey))
            return Results.BadRequest(new { error = "license key required" });

        var key = licenseKey.Trim().ToUpperInvariant();
        var lic = await db.Licenses
            .Where(l => l.LicenseKey == key)
            .Select(l => new { l.LicenseKey, l.Email, l.Revoked, l.Plan, l.ExpiresAt, l.CreatedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (lic is null) return Results.NotFound(new { error = "license not found" });
        if (lic.Revoked) return Results.BadRequest(new { error = "this license has been revoked" });
        if (string.IsNullOrWhiteSpace(lic.Email))
            return Results.BadRequest(new { error = "no email address on file for this license" });

        // For resends we don't know the original purchase amount. Look up the
        // order; if not found (e.g. admin-minted via AdminApp), fall back to
        // the plan's catalog price.
        var matchingOrder = await db.Orders
            .Where(o => o.LicenseKey == lic.LicenseKey)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.AmountUsd })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        decimal amountUsd = matchingOrder?.AmountUsd
                         ?? (OrderService.PlanPrices.TryGetValue(lic.Plan, out var p) ? p : 0m);

        var ok = await email.SendLicenseEmailAsync(
            toEmail: lic.Email,
            licenseKey: lic.LicenseKey,
            plan: lic.Plan,
            amountUsd: amountUsd,
            ct: ct).ConfigureAwait(false);

        if (!ok)
        {
            log.LogError("Resend failed for license {Key}", lic.LicenseKey);
            return Results.Problem(
                detail: "Email delivery failed. Please email support@limitlesscopier.com.",
                statusCode: 502);
        }

        log.LogInformation("License email re-sent for {Key} to {Email}", lic.LicenseKey, MaskEmail(lic.Email));
        return Results.Ok(new
        {
            ok = true,
            message = "License key has been emailed to the address on file."
        });
    }

    // =========================================================
    // Helpers
    // =========================================================

    /// <summary>
    /// Show the email partially masked so the customer can verify the
    /// address on file (it's their license, they know what they typed)
    /// without us leaking it back wholesale into someone's screenshot
    /// or pasted dashboard URL.
    ///
    /// "nouman3470@gmail.com" -> "n*******0@gmail.com"
    /// </summary>
    private static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var at = email.IndexOf('@');
        if (at < 1) return email;
        var local = email[..at];
        var domain = email[at..];
        if (local.Length <= 2) return $"{local[0]}*{domain}";
        return $"{local[0]}{new string('*', Math.Max(1, local.Length - 2))}{local[^1]}{domain}";
    }
}
