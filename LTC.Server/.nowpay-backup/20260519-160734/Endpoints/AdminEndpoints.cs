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

namespace LTC.Server.Endpoints;

/// <summary>
/// Admin endpoints — protected by a static bearer token. The token is
/// configured via "Admin:BearerToken" in appsettings.json (or the
/// LTC_ADMIN_TOKEN env var). If unset, the admin endpoints are
/// disabled entirely — failing closed for safety.
///
/// In Pass 2 these will be called by the desktop Admin app to replace
/// the current flow where it mints .lic files directly against the
/// private key. The server becomes the single source of truth for
/// what's been issued.
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
            var auth = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
            if (!auth.StartsWith("Bearer "))
                return Results.Unauthorized();
            var supplied = auth["Bearer ".Length..].Trim();
            // Constant-time compare to avoid timing attacks (admin token is
            // short and bearer leaks via process listing aren't the issue,
            // but it's free defense).
            if (!CryptographicEquals(supplied, token!))
                return Results.Unauthorized();
            return await next(ctx);
        });

        grp.MapPost("/keys/issue",    IssueKey);
        grp.MapPost("/keys/{key}/revoke", RevokeKey);
        grp.MapGet ("/keys",           ListKeys);
        grp.MapGet ("/keys/{key}",     KeyDetails);
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
    private static async Task<IResult> IssueKey(
        [FromBody] IssueKeyRequest req, LicensingDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return Results.BadRequest(new { error = "Email required" });
        if (string.IsNullOrWhiteSpace(req.Plan))
            return Results.BadRequest(new { error = "Plan required" });

        var key = GenerateLicenseKey(req.Plan);
        var lic = new License
        {
            LicenseKey = key,
            Email      = req.Email.Trim(),
            Plan       = req.Plan,
            ExpiresAt  = (req.Days is > 0) ? DateTime.UtcNow.AddDays(req.Days.Value) : null,
            Notes      = req.Notes,
        };
        db.Licenses.Add(lic);
        await db.SaveChangesAsync();

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

    /// <summary>Generate a customer-friendly license key.</summary>
    private static string GenerateLicenseKey(string plan)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
        var planTag = plan.ToUpperInvariant();
        if (planTag.Length > 4) planTag = planTag[..4];

        var rng = RandomNumberGenerator.Create();
        var sb = new StringBuilder("LTC-").Append(planTag);
        for (int g = 0; g < 3; g++)
        {
            sb.Append('-');
            for (int c = 0; c < 4; c++)
            {
                var buf = new byte[1];
                rng.GetBytes(buf);
                sb.Append(alphabet[buf[0] % alphabet.Length]);
            }
        }
        return sb.ToString();
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
