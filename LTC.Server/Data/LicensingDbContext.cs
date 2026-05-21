using Microsoft.EntityFrameworkCore;
using LTC.Server.Models;

namespace LTC.Server.Data;

/// <summary>
/// SQLite database for the activation server.
///
/// Schema design notes:
///   - One License → zero or one Activation (1:0..1)
///   - Many RequestLog entries per License (fraud detection)
///   - Unique index on LicenseKey so duplicate inserts fail loudly
///   - Indexes on FingerprintFull (Activation) and License.LicenseKey for
///     the lookups that happen on every customer hit.
///
/// === NOWPAY addition ===
///   - Orders table: one row per checkout attempt. Tracks the lifecycle
///     from "customer clicked Pay" through "NowPayments confirmed payment"
///     to "license issued and emailed". Indexed on OrderId (PK), Email,
///     Status, CreatedAt. EnsureCreated() at startup auto-creates the table.
///
/// === DASHBOARD/AFFILIATE addition ===
///   - Affiliates table: one row per License, generated when the license
///     issues. Code (slug) is claimed by the customer from the dashboard.
///   - DiscountCodes: admin-created codes (percent or flat).
///   - CodeRedemptions: audit log of every code used on a paid order.
///   - Commissions: one row per affiliate-attributed paid sale.
///
///   IMPORTANT — EnsureCreated() will NOT add these new tables to an
///   existing licenses.db. The startup code in Program.cs calls
///   <see cref="SchemaUpgrade.RunAsync"/> right after EnsureCreated() to
///   add the new tables via CREATE TABLE IF NOT EXISTS. Fresh deploys
///   get all tables from EnsureCreated; existing deploys get the
///   pre-existing ones from EnsureCreated (which no-ops because the DB
///   file exists) plus the new ones from SchemaUpgrade.
/// </summary>
public class LicensingDbContext : DbContext
{
    public LicensingDbContext(DbContextOptions<LicensingDbContext> options) : base(options) { }

    public DbSet<License> Licenses => Set<License>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    // === NOWPAY: BEGIN ===
    public DbSet<Order> Orders => Set<Order>();
    // === NOWPAY: END ===

    // === DASHBOARD: BEGIN ===
    public DbSet<Affiliate> Affiliates => Set<Affiliate>();
    public DbSet<DiscountCode> DiscountCodes => Set<DiscountCode>();
    public DbSet<CodeRedemption> CodeRedemptions => Set<CodeRedemption>();
    public DbSet<Commission> Commissions => Set<Commission>();
    // === DASHBOARD: END ===

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // License: unique key globally
        mb.Entity<License>()
          .HasIndex(l => l.LicenseKey)
          .IsUnique();

        // Activation: indexed by fingerprint for the /activate lookup
        mb.Entity<Activation>()
          .HasIndex(a => a.FingerprintFull);

        // One License has at most one Activation
        mb.Entity<License>()
          .HasOne(l => l.Activation)
          .WithOne(a => a.License)
          .HasForeignKey<Activation>(a => a.LicenseId)
          .OnDelete(DeleteBehavior.Cascade);

        // RequestLog: append-only, indexed by license + time
        mb.Entity<RequestLog>()
          .HasIndex(r => new { r.LicenseKey, r.At });

        // === NOWPAY: BEGIN - Orders entity config ===
        mb.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.OrderId).HasMaxLength(40);
            e.Property(x => x.InvoiceId).HasMaxLength(64);
            e.Property(x => x.PaymentId).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.Plan).HasMaxLength(20).IsRequired();
            e.Property(x => x.AmountUsd).HasPrecision(18, 2);
            e.Property(x => x.PayCurrency).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.LicenseKey).HasMaxLength(40);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });
        // === NOWPAY: END ===

        // === DASHBOARD: BEGIN - Affiliate entity config ===
        mb.Entity<Affiliate>(e =>
        {
            e.ToTable("Affiliates");
            e.HasKey(x => x.Id);
            e.Property(x => x.TotalEarnedUsd).HasPrecision(18, 2);
            e.Property(x => x.TotalPaidUsd).HasPrecision(18, 2);

            // One License -> at most one Affiliate (cascade delete on license removal)
            e.HasOne(x => x.License)
             .WithOne()
             .HasForeignKey<Affiliate>(x => x.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.LicenseId).IsUnique();
            // Code is nullable; unique only when set. SQLite supports partial indexes
            // but EF Core 8 abstraction is fiddly — we instead enforce uniqueness
            // at the application layer (the claim endpoint takes a row-lock equivalent).
            e.HasIndex(x => x.Code);
        });

        mb.Entity<DiscountCode>(e =>
        {
            e.ToTable("DiscountCodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(32).IsRequired();
            e.Property(x => x.DiscountFlatUsd).HasPrecision(18, 2);
            e.Property(x => x.MinPurchaseUsd).HasPrecision(18, 2);
            e.HasIndex(x => x.Code).IsUnique();
        });

        mb.Entity<CodeRedemption>(e =>
        {
            e.ToTable("CodeRedemptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(40).IsRequired();
            e.Property(x => x.CodeString).HasMaxLength(32).IsRequired();
            e.Property(x => x.DiscountAmountUsd).HasPrecision(18, 2);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.DiscountCodeId);
            e.HasIndex(x => x.AffiliateId);
        });

        mb.Entity<Commission>(e =>
        {
            e.ToTable("Commissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderId).HasMaxLength(40).IsRequired();
            e.Property(x => x.Plan).HasMaxLength(20).IsRequired();
            e.Property(x => x.OrderAmountUsd).HasPrecision(18, 2);
            e.Property(x => x.CommissionAmountUsd).HasPrecision(18, 2);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();

            e.HasOne(x => x.Affiliate)
             .WithMany()
             .HasForeignKey(x => x.AffiliateId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.AffiliateId);
            e.HasIndex(x => x.OrderId).IsUnique();   // one commission per order, max
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.EligibleAt);
        });
        // === DASHBOARD: END ===
    }
}
