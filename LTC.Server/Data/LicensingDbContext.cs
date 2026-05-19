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
    }
}
