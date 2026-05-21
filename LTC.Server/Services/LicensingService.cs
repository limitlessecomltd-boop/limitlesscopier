using System.Security.Cryptography;
using System.Text;
using LTC.Server.Data;
using LTC.Server.Models;

namespace LTC.Server.Services;

/// <summary>
/// Shared license issuance logic - called from BOTH:
///   1. AdminEndpoints (operator mints via AdminApp)
///   2. NowPaymentsWebhookEndpoint (automated mint after crypto payment)
///
/// Before this service existed, the issuance logic lived inline in
/// AdminEndpoints.IssueKey. That worked when there was only one caller,
/// but now we have webhook-triggered minting too, and we don't want to
/// duplicate the key generation + DB insert logic.
///
/// === DASHBOARD addition ===
/// Every new license now also gets an empty Affiliate row created in the
/// same transaction. The Affiliate.Code (slug) stays NULL until the
/// customer claims it from the dashboard. This way every customer is
/// implicitly an affiliate; they just have to claim a slug to start sharing.
/// </summary>
public class LicensingService
{
    private readonly LicensingDbContext _db;
    private readonly ILogger<LicensingService> _log;

    public LicensingService(LicensingDbContext db, ILogger<LicensingService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Issue a license. Persists to the Licenses table, returns the customer-facing key.
    /// Also creates an empty Affiliate row for the license.
    /// </summary>
    public async Task<string> IssueKeyAsync(
        string email,
        string plan,
        int? days,
        string? notes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email required", nameof(email));
        if (string.IsNullOrWhiteSpace(plan))
            throw new ArgumentException("Plan required", nameof(plan));

        var key = GenerateLicenseKey(plan);

        var lic = new License
        {
            LicenseKey = key,
            Email = email.Trim(),
            Plan = plan,
            ExpiresAt = (days is > 0) ? DateTime.UtcNow.AddDays(days.Value) : null,
            Notes = notes,
        };

        _db.Licenses.Add(lic);

        // === DASHBOARD: BEGIN ===
        // Every new license gets an Affiliate row. Code stays null until
        // the customer claims it from the dashboard. Adding it inside the
        // same SaveChangesAsync so it commits atomically with the License.
        var affiliate = new Affiliate
        {
            LicenseId = lic.Id,        // works because lic.Id was set to a new Guid in the entity ctor
            Code = null,
            CodeClaimedAt = null,
            TotalEarnedUsd = 0m,
            TotalPaidUsd = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Affiliates.Add(affiliate);
        // === DASHBOARD: END ===

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Issued license {Key} for {Email} (plan={Plan}, days={Days}) + affiliate slot",
            key, email, plan, days);
        return key;
    }

    /// <summary>
    /// Customer-friendly license key generation.
    /// Format: LTC-{PLAN_PREFIX}-XXXX-XXXX-XXXX with crockford-ish alphabet.
    /// Same algorithm as the original AdminEndpoints generator.
    /// </summary>
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
}
