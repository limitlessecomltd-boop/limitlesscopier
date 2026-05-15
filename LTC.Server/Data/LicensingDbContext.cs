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
/// SQLite was chosen for simplicity. A single ~50KB-per-license file
/// scales to ~20k licenses easily, far past what a single-operator
/// business is likely to hit. Migration to Postgres is a one-line
/// config change in Program.cs when the time comes.
/// </summary>
public class LicensingDbContext : DbContext
{
    public LicensingDbContext(DbContextOptions<LicensingDbContext> options) : base(options) { }

    public DbSet<License> Licenses => Set<License>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // The license key must be globally unique. SQLite enforces this
        // at the DB layer — any attempt to insert a duplicate throws
        // SqliteException with constraint code SQLITE_CONSTRAINT_UNIQUE.
        mb.Entity<License>()
          .HasIndex(l => l.LicenseKey)
          .IsUnique();

        // Fast lookup: "find activation for fingerprint X" during /activate
        mb.Entity<Activation>()
          .HasIndex(a => a.FingerprintFull);

        // One License has at most one Activation. EF Core uses the
        // foreign key on Activation.LicenseId; we want cascade-delete
        // so revoking a license removes its activation entry too.
        mb.Entity<License>()
          .HasOne(l => l.Activation)
          .WithOne(a => a.License)
          .HasForeignKey<Activation>(a => a.LicenseId)
          .OnDelete(DeleteBehavior.Cascade);

        // Logs are append-only; index by License + time for fraud queries
        mb.Entity<RequestLog>()
          .HasIndex(r => new { r.LicenseKey, r.At });

        // SQLite-specific: DateTime stored as ISO 8601 strings so the
        // raw .db file is human-readable.
        // (EF Core's default for SQLite already does this.)
    }
}
