using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using LTC.Server.Data;
using LTC.Server.Models;
using LTC.Server.Services;

namespace LTC.Server.Endpoints;

/// <summary>
/// Maps the four customer-facing endpoints onto a WebApplication.
/// Each handler is small and intentionally similar in shape so the
/// security-relevant logic is easy to audit:
///   1. Lookup license by key
///   2. Sanity-check (revoked / expired)
///   3. Per-endpoint policy (fingerprint match etc.)
///   4. Mutate DB inside a transaction
///   5. Log the request to RequestLog
///   6. Return result + optional signed token
///
/// All four also log via RequestLog so we have a permanent audit trail
/// of every key seen + every fingerprint that ever tried to activate.
/// </summary>
public static class CustomerEndpoints
{
    /// <summary>Number of components that must match for fingerprint
    /// acceptance. 3 of 4 — same threshold as the desktop side.</summary>
    public const int FingerprintMatchRequired = 3;

    /// <summary>How long an issued token is "fresh" before the client is
    /// expected to heartbeat again. The client gets an additional
    /// <c>ActivationToken.HardLockSlack</c> (4 hours) past this before it
    /// refuses to start. So the effective offline limit is ~52 hours from
    /// the last successful server contact.
    ///
    /// Tradeoff:
    ///   - Lower → catches license sharing / cracks faster, but punishes
    ///     legitimate customers whose internet drops over a weekend.
    ///   - Higher → friendlier offline experience, but cracked binaries
    ///     can operate offline for longer between forced re-checks.
    /// 48 hours feels right for the prop-trading audience: most trade
    /// during weekdays with reliable connections, but the occasional
    /// weekend offline session shouldn't lock them out.</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromHours(48);

    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/activate",   HandleActivate);
        app.MapPost("/heartbeat",  HandleHeartbeat);
        app.MapPost("/deactivate", HandleDeactivate);

        // Useful health check for monitoring / Caddy uptime probes
        app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTime.UtcNow }));
    }

    // ============================================================
    // ACTIVATE — bind a license key to a machine for the first time
    // ============================================================
    private static async Task<IResult> HandleActivate(
        [FromBody] ActivateRequest req,
        LicensingDbContext db,
        TokenSigningService signer,
        EmailAlertService alerts,
        HttpContext http,
        ILogger<License> log)
    {
        var ip = ClientIp(http);
        var ua = http.Request.Headers.UserAgent.ToString();

        if (!ParseFingerprint(req.Fingerprint, out var bundle, out var fpError))
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                "bad_fingerprint", ip, ua);
            return Bad("bad_fingerprint", fpError);
        }

        var license = await db.Licenses
            .Include(l => l.Activation)
            .FirstOrDefaultAsync(l => l.LicenseKey == req.LicenseKey);

        if (license is null)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                "key_not_found", ip, ua);
            // Don't tell the attacker which keys exist — generic message
            return Bad("key_not_found", "License key not recognized.");
        }

        if (license.Revoked)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                "revoked", ip, ua);
            return Bad("revoked",
                "This license has been revoked. Contact support if this is unexpected.");
        }

        if (license.ExpiresAt is not null && license.ExpiresAt < DateTime.UtcNow)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                "expired", ip, ua);
            return Bad("expired",
                $"This license expired on {license.ExpiresAt:yyyy-MM-dd}.");
        }

        // ----- Already-activated logic -----
        if (license.Activation is not null)
        {
            var score = ActivationTokenCodec.MatchScore(
                FingerprintFromString(license.Activation.FingerprintFull),
                bundle);

            if (score >= FingerprintMatchRequired)
            {
                // Same machine reactivating (e.g. customer reinstalled).
                // Treat as success — refresh heartbeat + re-issue token.
                license.Activation.LastHeartbeatAt = DateTime.UtcNow;
                license.Activation.LastIp = ip;
                license.Activation.FingerprintFull = req.Fingerprint;
                await db.SaveChangesAsync();

                var token = BuildToken(license, bundle, req.Fingerprint);
                var bytes = signer.SignToken(token);

                await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                    "ok_reactivate", ip, ua);
                return Ok(bytes);
            }

            // DIFFERENT machine — refuse, alert the operator.
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate",
                "already_active_elsewhere", ip, ua);
            log.LogWarning("License {Key} attempted from new machine — already bound. " +
                "Old short={Old} new short=...{New}", license.LicenseKey,
                license.Activation.MachineShort, ShortFromFull(req.Fingerprint));

            _ = alerts.SendAsync(
                $"[Limitless] Suspicious activation attempt: {license.LicenseKey}",
                $"License: {license.LicenseKey}\n" +
                $"Email: {license.Email}\n" +
                $"Plan: {license.Plan}\n" +
                $"Already bound to: {license.Activation.MachineShort}\n" +
                $"New attempt from short: {ShortFromFull(req.Fingerprint)}\n" +
                $"IP: {ip}\n" +
                $"User agent: {ua}\n\n" +
                $"This is likely either license sharing, OR the customer changed PCs " +
                $"without deactivating. Review the activation log in the admin app.");

            return Bad("already_active_elsewhere",
                "This license is already activated on another machine. " +
                "If you've moved PCs, contact support to deactivate the old one.");
        }

        // ----- First-time activation -----
        var newAct = new Activation
        {
            LicenseId       = license.Id,
            FingerprintFull = req.Fingerprint,
            MachineShort    = ShortFromFull(req.Fingerprint),
            FirstSeenAt     = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            LastIp          = ip,
        };
        db.Activations.Add(newAct);
        await db.SaveChangesAsync();

        var firstToken = BuildToken(license, bundle, req.Fingerprint);
        var firstBytes = signer.SignToken(firstToken);

        await LogRequest(db, req.LicenseKey, req.Fingerprint, "activate", "ok", ip, ua);
        return Ok(firstBytes);
    }

    // ============================================================
    // HEARTBEAT — refresh an activation; same shape but doesn't bind
    // ============================================================
    private static async Task<IResult> HandleHeartbeat(
        [FromBody] HeartbeatRequest req,
        LicensingDbContext db,
        TokenSigningService signer,
        HttpContext http,
        ILogger<License> log)
    {
        var ip = ClientIp(http);
        var ua = http.Request.Headers.UserAgent.ToString();

        if (!ParseFingerprint(req.Fingerprint, out var bundle, out var fpError))
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "bad_fingerprint", ip, ua);
            return Bad("bad_fingerprint", fpError);
        }

        var license = await db.Licenses
            .Include(l => l.Activation)
            .FirstOrDefaultAsync(l => l.LicenseKey == req.LicenseKey);

        if (license is null)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "key_not_found", ip, ua);
            return Bad("key_not_found", "License key not recognized.");
        }

        if (license.Revoked)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "revoked", ip, ua);
            return Bad("revoked", "License revoked.");
        }

        if (license.ExpiresAt is not null && license.ExpiresAt < DateTime.UtcNow)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "expired", ip, ua);
            return Bad("expired", "License expired.");
        }

        if (license.Activation is null)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "not_activated", ip, ua);
            return Bad("not_activated",
                "License is not yet activated on any machine. Call /activate first.");
        }

        var score = ActivationTokenCodec.MatchScore(
            FingerprintFromString(license.Activation.FingerprintFull),
            bundle);
        if (score < FingerprintMatchRequired)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat",
                "fingerprint_mismatch", ip, ua);
            return Bad("fingerprint_mismatch",
                "This license is bound to a different machine.");
        }

        // Update + re-sign
        license.Activation.LastHeartbeatAt = DateTime.UtcNow;
        license.Activation.LastIp = ip;
        await db.SaveChangesAsync();

        var token = BuildToken(license, bundle, req.Fingerprint);
        var bytes = signer.SignToken(token);

        await LogRequest(db, req.LicenseKey, req.Fingerprint, "heartbeat", "ok", ip, ua);
        return Ok(bytes);
    }

    // ============================================================
    // DEACTIVATE — clear the binding so the customer can install elsewhere
    // ============================================================
    private static async Task<IResult> HandleDeactivate(
        [FromBody] DeactivateRequest req,
        LicensingDbContext db,
        HttpContext http,
        ILogger<License> log)
    {
        var ip = ClientIp(http);
        var ua = http.Request.Headers.UserAgent.ToString();

        if (!ParseFingerprint(req.Fingerprint, out var bundle, out var fpError))
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "deactivate",
                "bad_fingerprint", ip, ua);
            return Bad("bad_fingerprint", fpError);
        }

        var license = await db.Licenses
            .Include(l => l.Activation)
            .FirstOrDefaultAsync(l => l.LicenseKey == req.LicenseKey);

        if (license?.Activation is null)
        {
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "deactivate",
                "not_activated", ip, ua);
            // Idempotent — already deactivated is a success
            return Results.Ok(new ActivationResponse
            {
                Ok = true,
                Message = "License was not currently activated. Nothing to do."
            });
        }

        var score = ActivationTokenCodec.MatchScore(
            FingerprintFromString(license.Activation.FingerprintFull),
            bundle);
        if (score < FingerprintMatchRequired)
        {
            // The fingerprint that's asking to deactivate isn't the one
            // that's bound. Refuse — otherwise anyone who knows the key
            // could "steal" the activation slot from the real holder.
            await LogRequest(db, req.LicenseKey, req.Fingerprint, "deactivate",
                "fingerprint_mismatch", ip, ua);
            return Bad("fingerprint_mismatch",
                "Deactivate can only be performed from the machine that currently holds the activation.");
        }

        db.Activations.Remove(license.Activation);
        await db.SaveChangesAsync();

        await LogRequest(db, req.LicenseKey, req.Fingerprint, "deactivate", "ok", ip, ua);
        return Results.Ok(new ActivationResponse
        {
            Ok = true,
            Message = "License deactivated. You can now activate on a different machine."
        });
    }

    // ============================================================
    // HELPERS
    // ============================================================

    private static bool ParseFingerprint(string raw, out FingerprintBundle bundle, out string error)
    {
        bundle = new FingerprintBundle("", "", "", "");
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Fingerprint missing.";
            return false;
        }
        var parts = raw.Trim().Split('-');
        if (parts.Length != 4 || parts.Any(p => p.Length != 32))
        {
            error = "Fingerprint must be 4 dash-separated 32-char hex chunks.";
            return false;
        }
        bundle = new FingerprintBundle(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    private static FingerprintBundle FingerprintFromString(string s)
    {
        var parts = s.Split('-');
        // Defensive: if stored data is malformed, return blanks (won't match)
        if (parts.Length != 4) return new FingerprintBundle("", "", "", "");
        return new FingerprintBundle(parts[0], parts[1], parts[2], parts[3]);
    }

    private static string ShortFromFull(string full)
    {
        var clean = full.Replace("-", "");
        return (clean.Length >= 8 ? clean[..8] : clean.PadRight(8, '0'))
            .ToUpperInvariant();
    }

    private static string ClientIp(HttpContext http)
    {
        // X-Forwarded-For takes priority because we run behind a proxy
        // (Railway in production; was Caddy on the old VPS plan).
        var xff = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
            return xff.Split(',')[0].Trim();
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>Append a row to RequestLog. We deliberately don't use a
    /// transaction here — log writes shouldn't block the response.</summary>
    private static async Task LogRequest(LicensingDbContext db,
        string? key, string? fp, string endpoint, string result,
        string? ip, string? ua)
    {
        try
        {
            db.RequestLogs.Add(new RequestLog
            {
                LicenseKey      = key,
                FingerprintFull = fp,
                Endpoint        = endpoint,
                Result          = result,
                Ip              = ip,
                UserAgent       = ua?.Length > 200 ? ua[..200] : ua,
                At              = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Log write failed — don't fail the whole request because of it
        }
    }

    private static ActivationToken BuildToken(License license, FingerprintBundle bundle, string fullFp)
    {
        var issued = DateTime.UtcNow;
        var expires = license.ExpiresAt ?? DateTime.MaxValue;

        // Heartbeat 48 hours out (configurable via HeartbeatWindow). The
        // client adds its own 4-hour hard-lock slack on top — so total
        // offline tolerance from last successful server contact is ~52h.
        // See ActivationToken.HardLockSlack on the client side.
        var heartbeat = issued + HeartbeatWindow;

        // If the license itself expires sooner, the token expires sooner.
        if (heartbeat > expires) heartbeat = expires;

        return new ActivationToken
        {
            LicenseKey      = license.LicenseKey,
            Email           = license.Email,
            Plan            = license.Plan,
            IssuedUtc       = issued,
            ExpiresUtc      = expires,
            HeartbeatDueUtc = heartbeat,
            Fingerprint     = bundle,
        };
    }

    private static IResult Ok(byte[] tokenBytes) =>
        Results.Ok(new ActivationResponse
        {
            Ok = true,
            TokenBase64 = Convert.ToBase64String(tokenBytes),
            Message = "Activation successful.",
        });

    private static IResult Bad(string code, string message) =>
        Results.Ok(new ActivationResponse   // 200 with Ok=false — easier for clients
        {
            Ok = false,
            ErrorCode = code,
            Message = message,
        });
}
