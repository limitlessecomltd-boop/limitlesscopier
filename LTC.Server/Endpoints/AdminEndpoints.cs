using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LTC.Server.Data;
using LTC.Server.Models;
using LTC.Server.Services;

namespace LTC.Server.Endpoints;

/// <summary>
/// Admin endpoints — protected by a static bearer token. The token is
/// configured via "Admin:BearerToken" in appsettings.json (or the
/// LTC_ADMIN_TOKEN env var). If unset, the admin endpoints are
/// disabled entirely — failing closed for safety.
///
/// === NOWPAY refactor note ===
/// IssueKey previously contained the key-generation + persist logic inline.
/// That logic is now in LicensingService.IssueKeyAsync so both this endpoint
/// AND the NowPayments webhook can call it. Behavior is identical: same
/// key format, same alphabet, same License fields, same response shape.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var token = app.Configuration["Admin:BearerToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            app.Logger.LogWarning("Admin:BearerToken not configured — admin endpoints disabled.");
            return;
        }
        var grp = app.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            // Let CORS preflight (OPTIONS) through untouched — the browser sends
            // it with no Authorization header, and the CORS middleware must be
            // allowed to answer it. Blocking it here with 401 breaks the preflight
            // and the browser refuses the real request.
            if (HttpMethods.IsOptions(ctx.HttpContext.Request.Method))
                return await next(ctx);

            var auth = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
            if (!auth.StartsWith("Bearer "))
                return Results.Unauthorized();
            var supplied = auth["Bearer ".Length..].Trim();
            if (!CryptographicEquals(supplied, token!))
                return Results.Unauthorized();
            return await next(ctx);
        });

        grp.MapPost("/keys/issue",        IssueKey);
        grp.MapPost("/keys/{key}/revoke", RevokeKey);
        grp.MapGet ("/keys",              ListKeys);
        grp.MapGet ("/keys/{key}",        KeyDetails);
    }

    // -------- ISSUE --------

    public class IssueKeyRequest
    {
        public string Email { get; set; } = "";
        public string Plan { get; set; } = "Lifetime";
        public int? Days { get; set; }
        public string? Notes { get; set; }
    }

    public class IssueKeyResponse
    {
        public bool Ok { get; set; }
        public string LicenseKey { get; set; } = "";
        public string Email { get; set; } = "";
        public string Plan { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
    }

    // === NOWPAY: REFACTORED - delegates to LicensingService ===
    // Previously this method contained the inline logic to generate a key
    // and insert a License row. That logic now lives in LicensingService
    // so the NowPayments webhook can call the same code path.
    // External behavior (request shape, response shape, status codes) is UNCHANGED.
    private static async Task<IResult> IssueKey(
        [FromBody] IssueKeyRequest req,
        LicensingService licensing,
        LicensingDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "Email required" });
        if (string.IsNullOrWhiteSpace(req.Plan))
            return Results.BadRequest(new { error = "Plan required" });

        var key = await licensing.IssueKeyAsync(
            email: req.Email,
            plan: req.Plan,
            days: req.Days,
            notes: req.Notes);

        // Re-read the row to surface ExpiresAt (set inside LicensingService)
        var lic = await db.Licenses.AsNoTracking()
            .FirstAsync(l => l.LicenseKey == key);

        return Results.Ok(new IssueKeyResponse
        {
            Ok = true,
            LicenseKey = key,
            Email = lic.Email,
            Plan = lic.Plan,
            ExpiresAt = lic.ExpiresAt,
        });
    }

    // -------- REVOKE --------

    public class RevokeRequest { public string? Reason { get; set; } }

    private static async Task<IResult> RevokeKey(
        string key, [FromBody] RevokeRequest? body, LicensingDbContext db)
    {
        var lic = await db.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == key);
        if (lic is null) return Results.NotFound();

        lic.Revoked = true;
        lic.RevokedAt = DateTime.UtcNow;
        lic.RevokedReason = body?.Reason ?? "Revoked by admin";

        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true, revokedAt = lic.RevokedAt });
    }

    // -------- LIST --------

    private static async Task<IResult> ListKeys(LicensingDbContext db)
    {
        var rows = await db.Licenses
            .OrderByDescending(l => l.CreatedAt)
            .Take(500)
            .Select(l => new {
                l.LicenseKey,
                l.Email,
                l.Plan,
                l.ExpiresAt,
                l.CreatedAt,
                l.Revoked,
                isActivated = (l.Activation != null),
            })
            .ToListAsync();
        return Results.Ok(rows);
    }

    // -------- DETAILS --------

    private static async Task<IResult> KeyDetails(string key, LicensingDbContext db)
    {
        var lic = await db.Licenses
            .Include(l => l.Activation)
            .FirstOrDefaultAsync(l => l.LicenseKey == key);
        if (lic is null) return Results.NotFound();

        return Results.Ok(new {
            lic.LicenseKey,
            lic.Email,
            lic.Plan,
            lic.ExpiresAt,
            lic.CreatedAt,
            lic.Revoked,
            lic.RevokedAt,
            lic.RevokedReason,
            lic.Notes,
            activation = lic.Activation == null ? null : new {
                lic.Activation.MachineShort,
                lic.Activation.FirstSeenAt,
                lic.Activation.LastHeartbeatAt,
                lic.Activation.LastIp,
            }
        });
    }

    // -------- HELPERS --------

    // GenerateLicenseKey was REMOVED - logic moved to LicensingService.

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
