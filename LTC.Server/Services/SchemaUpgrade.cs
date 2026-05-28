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
///   Instead: idempotent raw SQL. Each CREATE TABLE IF NOT EXISTS no-ops
///   if the table already exists; same for indexes. Safe to run on every
///   startup.
///
/// === Zip 3 addition ===
///   Adds two columns to the existing Orders table:
///     - AppliedCode      (TEXT, nullable) - the literal code typed by buyer
///     - DiscountAmountUsd (TEXT/decimal)   - discount in USD (0 if affiliate or no code)
///   SQLite supports ALTER TABLE ADD COLUMN. Older SQLite (pre-3.35) doesn't
///   support IF NOT EXISTS on ALTER, so we wrap in try/catch — duplicate-column
///   error on subsequent runs is logged and ignored.
/// </summary>
public static class SchemaUpgrade
{
    public static async Task RunAsync(LicensingDbContext db, ILogger log, CancellationToken ct = default)
    {
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

            // === ZIP 3 ADDITIONS ===
            // Two new columns on existing Orders table. ALTER TABLE without IF NOT EXISTS
            // (older SQLite doesn't support it). On second+ runs these will throw
            // "duplicate column name: ..." which the try/catch in the loop below
            // turns into a logged warning, not a startup failure.
            @"ALTER TABLE ""Orders"" ADD COLUMN ""AppliedCode"" TEXT NULL;",
            @"ALTER TABLE ""Orders"" ADD COLUMN ""DiscountAmountUsd"" TEXT NOT NULL DEFAULT '0';",

            // === PAYOUT: affiliate payout requests + global settings ===
            @"CREATE TABLE IF NOT EXISTS ""PayoutRequests"" (
                ""Id""             TEXT     NOT NULL CONSTRAINT ""PK_PayoutRequests"" PRIMARY KEY,
                ""AffiliateId""     TEXT     NOT NULL,
                ""AmountUsd""       TEXT     NOT NULL DEFAULT '0',
                ""Status""          TEXT     NOT NULL DEFAULT 'requested',
                ""PayoutDetails""   TEXT     NULL,
                ""RequestedAt""     TEXT     NOT NULL,
                ""PaidAt""          TEXT     NULL,
                ""RejectedAt""      TEXT     NULL,
                ""AdminNotes""      TEXT     NULL,
                CONSTRAINT ""FK_PayoutRequests_Affiliates_AffiliateId""
                    FOREIGN KEY (""AffiliateId"") REFERENCES ""Affiliates"" (""Id"") ON DELETE CASCADE
            );",
            @"CREATE INDEX IF NOT EXISTS ""IX_PayoutRequests_AffiliateId"" ON ""PayoutRequests"" (""AffiliateId"");",
            @"CREATE INDEX IF NOT EXISTS ""IX_PayoutRequests_Status"" ON ""PayoutRequests"" (""Status"");",
            @"CREATE TABLE IF NOT EXISTS ""AppSettings"" (
                ""Key""        TEXT     NOT NULL CONSTRAINT ""PK_AppSettings"" PRIMARY KEY,
                ""Value""      TEXT     NULL,
                ""UpdatedAt""  TEXT     NOT NULL DEFAULT '1970-01-01 00:00:00'
            );",
        };

        int applied = 0;
        int skipped = 0;
        foreach (var sql in statements)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
                applied++;
            }
            catch (Exception ex)
            {
                // For the ALTER TABLE statements on second runs, this catches
                // "duplicate column name" — log as Info, not Error. For genuine
                // SQL problems, the message will tell us what went wrong.
                if (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                {
                    log.LogInformation("SchemaUpgrade skip (already applied): {Sql}",
                        sql.Substring(0, Math.Min(80, sql.Length)));
                    skipped++;
                }
                else
                {
                    log.LogError(ex, "SchemaUpgrade statement failed (continuing): {Sql}",
                        sql.Substring(0, Math.Min(80, sql.Length)));
                }
            }
        }

        log.LogInformation("SchemaUpgrade complete: {Applied} applied, {Skipped} already-present, {Total} total",
            applied, skipped, statements.Length);
    }
}
