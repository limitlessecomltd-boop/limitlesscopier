using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.RateLimiting;
using LTC.Server.Data;
using LTC.Server.Endpoints;
using LTC.Server.Services;

// =========================================================
// Bootstrap Serilog as early as possible — anything during
// builder setup gets captured.
// =========================================================
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/server-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("Starting Limitless activation server");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // =========================================================
    // SERVICES
    // =========================================================

    // SQLite database. Path is configurable so production VPS can put
    // the DB file under /var/lib/limitless and run a daily backup.
    var dbPath = builder.Configuration["Database:Path"] ?? "licenses.db";
    builder.Services.AddDbContext<LicensingDbContext>(opts =>
        opts.UseSqlite($"Data Source={dbPath}"));

    // Token signer (loads private key at construction — will throw if missing)
    builder.Services.AddSingleton<TokenSigningService>();

    // Email alerts (loads SMTP config; no-ops gracefully if unset)
    builder.Services.AddSingleton<EmailAlertService>();

    // Rate limiting — built into ASP.NET Core 8. Each policy is
    // attached to specific endpoints below. The defaults below allow
    // normal customer traffic; aggressive on the activation path
    // (a single IP shouldn't be activating 100 licenses an hour).
    builder.Services.AddRateLimiter(opts =>
    {
        // "customer" policy — applies to /activate, /heartbeat, /deactivate.
        // 60 requests per minute per IP, ~1 every second sustained.
        opts.AddPolicy("customer", http =>
        {
            var xff = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            var partitionKey = !string.IsNullOrWhiteSpace(xff)
                ? xff.Split(',')[0].Trim()
                : (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: partitionKey,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });
        opts.RejectionStatusCode = 429;
    });

    // =========================================================
    // BUILD + PIPELINE
    // =========================================================
    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseRateLimiter();

    // Auto-create / migrate DB on startup. For a project this small with
    // SQLite, this is cleaner than separate migrate-on-deploy steps.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        db.Database.EnsureCreated();
        Log.Information("Database ready at {Path}",
            app.Configuration["Database:Path"] ?? "licenses.db");
    }

    // =========================================================
    // ENDPOINTS
    // =========================================================
    var customerGroup = app.MapGroup("").RequireRateLimiting("customer");
    customerGroup.MapCustomerEndpoints();

        // Health check � Railway probes this every 30 seconds to confirm
    // the container is live. Anonymous, no rate limit, no auth.
    app.MapGet("/health", () => Results.Ok(new {
        status  = "ok",
        time    = DateTime.UtcNow
    }));

    app.MapAdminEndpoints();   // bearer-protected; sets up its own group

    Log.Information("Activation server listening");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Activation server failed to start");
}
finally
{
    Log.CloseAndFlush();
}
