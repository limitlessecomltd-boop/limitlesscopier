using Microsoft.EntityFrameworkCore;
using LTC.Server.Data;

namespace LTC.Server.Services;

/// <summary>
/// Adds the dashboard/affiliate tables to an existing licenses.db.
///
/// Why this exists (the EnsureCreated() gotcha):
///   EnsureCreated() short-circuits if the database file already exists.
///   It does NOT add missing tables. So when we add new DbSets to the
///   context, those tables won't materialize on existing deployments
///   (like the production server) — only on a fresh empty DB.
///
///   We don't want to switch to proper EF migrations right now because:
///     1. The existing DB has no __EFMigrationsHistory entries (it was
///        created by EnsureCreated). Adding migrations from this state
///        requires a delicate dance — manual INSERT into __EFMigrationsHistory
///        to mark the baseline as applied, otherwise the next migration
///        tries to re-create the Licenses table and crashes.
///     2. Migrations tooling has to be installed everywhere we deploy
///        (Railway build container, dev box, etc.).
///
///   Instead: idempotent raw SQL. Each CREATE TABLE IF NOT EXISTS no-ops
///   if the table already exists; same for indexes. Safe to run on every
///   startup. When we eventually move to proper migrations, we baseline
///   the current schema (including these tables) and these CREATE
///   statements become dead code we can delete.
///
/// SQLite specifics:
///   - TEXT for strings (any length cap is informational, not enforced)
///   - REAL for decimals (we store with HasPrecision in the model but
///     SQLite ignores that — REAL is double-precision, fine for $0.01
///     resolution within sensible amount ranges)
///   - INTEGER for ints
///   - TEXT for DateTime (EF Core stores them as ISO 8601 strings)
///   - TEXT for Guid (EF Core stores them as text with hyphens by default)
/// </summary>
public static class SchemaUpgrade
{
    public static async Task RunAsync(LicensingDbContext db, ILogger log, CancellationToken ct = default)
    {
        // Each statement is its own ExecuteSqlRawAsync call. SQLite doesn't
        // support multi-statement strings via the EF provider in all cases,
        // so we send them one at a time. Slight overhead, much clearer errors.
        var statements = new[]
        {
            // --- Affiliates ---
            @"CREATE TABLE IF NOT EXISTS ""Affiliates"" (
                ""Id""              TEXT       NOT NULL CONSTRAINT ""PK_Affiliates"" PRIMARY KEY,
                ""LicenseId""       TEXT       NOT NULL,
                ""Code""            TEXT       NULL,
                ""CodeClaimedAt""   TEXT       NULL,
                ""TotalEarnedUsd""  TEXT       NOT NULL DEFAULT '0',
                ""TotalPaidUsd""    TEXT       NOT NULL DEFAULT '0',
                ""CreatedAt""       TEXT       NOT NULL,
                CONSTRAINT ""FK_Affiliates_Licenses_LicenseId""
                    FOREIGN KEY (""LicenseId"") REFERENCES ""Licenses"" (""Id"") ON DELETE CASCADE
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Affiliates_LicenseId"" ON ""Affiliates"" (""LicenseId"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_Affiliates_Code"" ON ""Affiliates"" (""Code"");",

            // --- DiscountCodes ---
            @"CREATE TABLE IF NOT EXISTS ""DiscountCodes"" (
                ""Id""               TEXT      NOT NULL CONSTRAINT ""PK_DiscountCodes"" PRIMARY KEY,
                ""Code""             TEXT      NOT NULL,
                ""DiscountPercent""  INTEGER   NOT NULL DEFAULT 0,
                ""DiscountFlatUsd""  TEXT      NOT NULL DEFAULT '0',
                ""MaxUses""          INTEGER   NULL,
                ""UsedCount""        INTEGER   NOT NULL DEFAULT 0,
                ""ExpiresAt""        TEXT      NULL,
                ""MinPurchaseUsd""   TEXT      NULL,
                ""Enabled""          INTEGER   NOT NULL DEFAULT 1,
                ""CreatedAt""        TEXT      NOT NULL,
                ""Notes""            TEXT      NULL
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DiscountCodes_Code"" ON ""DiscountCodes"" (""Code"");",

            // --- CodeRedemptions ---
            @"CREATE TABLE IF NOT EXISTS ""CodeRedemptions"" (
                ""Id""                  TEXT     NOT NULL CONSTRAINT ""PK_CodeRedemptions"" PRIMARY KEY,
                ""OrderId""             TEXT     NOT NULL,
                ""DiscountCodeId""      TEXT     NULL,
                ""AffiliateId""         TEXT     NULL,
                ""CodeString""          TEXT     NOT NULL,
                ""DiscountAmountUsd""   TEXT     NOT NULL DEFAULT '0',
                ""RedeemedAt""          TEXT     NOT NULL
            );",
            @"CREATE INDEX IF NOT EXISTS ""IX_CodeRedemptions_OrderId"" ON ""CodeRedemptions"" (""OrderId"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_CodeRedemptions_DiscountCodeId"" ON ""CodeRedemptions"" (""DiscountCodeId"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_CodeRedemptions_AffiliateId"" ON ""CodeRedemptions"" (""AffiliateId"");",

            // --- Commissions ---
            @"CREATE TABLE IF NOT EXISTS ""Commissions"" (
                ""Id""                    TEXT    NOT NULL CONSTRAINT ""PK_Commissions"" PRIMARY KEY,
                ""AffiliateId""           TEXT    NOT NULL,
                ""OrderId""               TEXT    NOT NULL,
                ""Plan""                  TEXT    NOT NULL,
                ""OrderAmountUsd""        TEXT    NOT NULL DEFAULT '0',
                ""CommissionAmountUsd""   TEXT    NOT NULL DEFAULT '0',
                ""Status""                TEXT    NOT NULL DEFAULT 'pending',
                ""PaidAt""                TEXT    NOT NULL,
                ""EligibleAt""            TEXT    NOT NULL,
                ""EarnedAt""              TEXT    NULL,
                ""PaidOutAt""             TEXT    NULL,
                ""CancelledAt""           TEXT    NULL,
                ""Notes""                 TEXT    NULL,
                CONSTRAINT ""FK_Commissions_Affiliates_AffiliateId""
                    FOREIGN KEY (""AffiliateId"") REFERENCES ""Affiliates"" (""Id"") ON DELETE CASCADE
            );",
            @"CREATE INDEX IF NOT EXISTS ""IX_Commissions_AffiliateId"" ON ""Commissions"" (""AffiliateId"");",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Commissions_OrderId"" ON ""Commissions"" (""OrderId"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_Commissions_Status"" ON ""Commissions"" (""Status"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_Commissions_EligibleAt"" ON ""Commissions"" (""EligibleAt"");",
        };

        int applied = 0;
        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
                applied++;
            }
            catch (Exception ex)
            {
                // Don't kill startup — log and continue. A failure on a CREATE IF NOT EXISTS
                // basically only happens if SQLite version is ancient or DB file is locked.
                log.LogError(ex, "SchemaUpgrade statement failed (continuing): {Sql}",
                    sql.Substring(0, Math.Min(60, sql.Length)));
            }
        }

        log.LogInformation("SchemaUpgrade complete: {Applied}/{Total} statements OK", applied, statements.Length);
    }
}
