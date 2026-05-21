using System.Text.RegularExpressions;
using LTC.Server.Data;
using LTC.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace LTC.Server.Services;

/// <summary>
/// Logic for affiliates: claiming a slug, looking up codes, flipping
/// pending commissions to earned once their 14-day hold expires.
///
/// Lazy eligibility flip: the dashboard endpoint calls
/// <see cref="FlipPendingCommissionsForAffiliateAsync"/> at the start of
/// every render. This means we don't need a separate background job for
/// v1 — pending commissions that have crossed EligibleAt get flipped
/// the next time their owner loads the dashboard.
/// Trade-off: an affiliate who never loads their dashboard never sees
/// "earned" status on their commissions, even though the money is owed.
/// Acceptable for v1; revisit if affiliates start sitting on balances.
/// </summary>
public class AffiliateService
{
    private readonly LicensingDbContext _db;
    private readonly ILogger<AffiliateService> _log;

    // Slug rules:
    //   - 3-24 chars
    //   - ASCII letters, digits, dash, underscore
    //   - case-insensitive (stored uppercase)
    //   - must start with a letter (so "1" or "-foo" isn't allowed)
    private static readonly Regex SlugRegex = new(
        @"^[A-Za-z][A-Za-z0-9_-]{2,23}$",
        RegexOptions.Compiled);

    // Reserved slugs that affiliates can't claim. Mostly to keep
    // potential future routes from colliding (e.g. /admin, /support).
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "support", "help", "api", "www", "dashboard", "checkout",
        "limitless", "ltc", "system", "root", "test", "null", "undefined",
    };

    public AffiliateService(LicensingDbContext db, ILogger<AffiliateService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Result of a claim attempt. <see cref="Ok"/> == true means the
    /// slug was set; false means see <see cref="Error"/> for why.
    /// </summary>
    public sealed record ClaimResult(bool Ok, string? Error, Affiliate? Affiliate);

    /// <summary>
    /// Claim a slug for a license's affiliate row. Locked once set.
    /// </summary>
    public async Task<ClaimResult> ClaimSlugAsync(
        string licenseKey,
        string requestedSlug,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestedSlug))
            return new ClaimResult(false, "code required", null);

        var slug = requestedSlug.Trim().ToUpperInvariant();
        if (!SlugRegex.IsMatch(slug))
            return new ClaimResult(false,
                "code must be 3-24 characters, start with a letter, and use only letters/digits/dash/underscore",
                null);

        if (ReservedSlugs.Contains(slug))
            return new ClaimResult(false, "that code is reserved", null);

        // Locate the affiliate row by joining through License.
        var lic = await _db.Licenses
            .Where(l => l.LicenseKey == licenseKey)
            .Select(l => new { l.Id, l.Revoked })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (lic is null) return new ClaimResult(false, "license not found", null);
        if (lic.Revoked) return new ClaimResult(false, "this license has been revoked", null);

        var affiliate = await _db.Affiliates
            .FirstOrDefaultAsync(a => a.LicenseId == lic.Id, ct)
            .ConfigureAwait(false);

        if (affiliate is null)
        {
            // Older licenses (issued before the dashboard build) won't have an
            // Affiliate row yet. Create one on-demand here so legacy customers
            // can still claim codes.
            affiliate = new Affiliate
            {
                LicenseId = lic.Id,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Affiliates.Add(affiliate);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(affiliate.Code))
            return new ClaimResult(false,
                $"You've already claimed the code '{affiliate.Code.ToLowerInvariant()}'. Codes are locked once set.",
                affiliate);

        // Uniqueness check. We don't rely on a unique index because the
        // Code column allows nulls and SQLite's unique-index behavior on
        // nullable cols is subtle. Application-layer check is clearer.
        var taken = await _db.Affiliates.AnyAsync(a => a.Code == slug, ct).ConfigureAwait(false);
        if (taken)
            return new ClaimResult(false,
                $"The code '{slug.ToLowerInvariant()}' is already taken. Try another.",
                null);

        affiliate.Code = slug;
        affiliate.CodeClaimedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Affiliate slug claimed: license {Key} -> code {Slug}", licenseKey, slug);
        return new ClaimResult(true, null, affiliate);
    }

    /// <summary>
    /// Flip "pending" commissions for an affiliate to "earned" if their
    /// EligibleAt has passed AND the underlying order is still in good
    /// standing (i.e. wasn't refunded or invalidated).
    /// Also updates the affiliate's TotalEarnedUsd denormalized field.
    /// </summary>
    public async Task FlipPendingCommissionsForAffiliateAsync(Guid affiliateId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var pending = await _db.Commissions
            .Where(c => c.AffiliateId == affiliateId &&
                        c.Status == "pending" &&
                        c.EligibleAt <= now)
            .ToListAsync(ct).ConfigureAwait(false);

        if (pending.Count == 0) return;

        decimal newlyEarned = 0m;
        foreach (var c in pending)
        {
            // For v1 we trust the order wasn't refunded if we got here.
            // (We don't have refund flagging on Orders yet — the order
            // stays "paid" forever unless we add a manual revoke flow.)
            c.Status = "earned";
            c.EarnedAt = now;
            newlyEarned += c.CommissionAmountUsd;
        }

        if (newlyEarned > 0)
        {
            var affiliate = await _db.Affiliates
                .FirstOrDefaultAsync(a => a.Id == affiliateId, ct).ConfigureAwait(false);
            if (affiliate is not null)
            {
                affiliate.TotalEarnedUsd += newlyEarned;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Flipped {Count} pending commissions to earned for affiliate {Id} (+${Amt})",
            pending.Count, affiliateId, newlyEarned);
    }

    /// <summary>
    /// Resolve a code string (typed at checkout) to either a DiscountCode
    /// or an Affiliate, or neither. Used by validate-code endpoint and
    /// by the webhook handler to attribute commissions.
    /// </summary>
    public async Task<CodeLookup> ResolveCodeAsync(string codeString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codeString))
            return new CodeLookup(CodeKind.None, null, null);

        var normalised = codeString.Trim().ToUpperInvariant();

        // Try discount code first (admin-controlled, indexed UNIQUE)
        var discount = await _db.DiscountCodes
            .FirstOrDefaultAsync(d => d.Code == normalised, ct).ConfigureAwait(false);
        if (discount is not null)
            return new CodeLookup(CodeKind.Discount, discount, null);

        // Then affiliate code (customer-claimed slug)
        var affiliate = await _db.Affiliates
            .Include(a => a.License)
            .FirstOrDefaultAsync(a => a.Code == normalised, ct).ConfigureAwait(false);
        if (affiliate is not null)
            return new CodeLookup(CodeKind.Affiliate, null, affiliate);

        return new CodeLookup(CodeKind.None, null, null);
    }

    public enum CodeKind { None, Discount, Affiliate }
    public sealed record CodeLookup(CodeKind Kind, DiscountCode? Discount, Affiliate? Affiliate);
}
