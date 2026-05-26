using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.RateLimiting;
using LTC.Server.Data;
using LTC.Server.Endpoints;
using LTC.Server.Models;
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

    var dbPath = builder.Configuration["Database:Path"] ?? "licenses.db";
    builder.Services.AddDbContext<LicensingDbContext>(opts =>
        opts.UseSqlite($"Data Source={dbPath}"));

    builder.Services.AddSingleton<TokenSigningService>();
    builder.Services.AddSingleton<EmailAlertService>();

    // === NOWPAY: BEGIN ===
    builder.Services.Configure<NowPaymentsOptions>(
        builder.Configuration.GetSection("NowPayments"));
    builder.Services.Configure<ResendOptions>(
        builder.Configuration.GetSection("Resend"));
    builder.Services.AddHttpClient<NowPaymentsClient>();
    builder.Services.AddHttpClient<EmailService>();
    builder.Services.AddScoped<OrderService>();
    builder.Services.AddScoped<LicensingService>();
    // === NOWPAY: END ===

    // === DASHBOARD: BEGIN ===
    builder.Services.AddScoped<AffiliateService>();
    // === ZIP 3: BEGIN ===
    builder.Services.AddScoped<CheckoutValidationService>();
    builder.Services.AddScoped<CommissionService>();
    // === ZIP 3: END ===
    // === DASHBOARD: END ===

    builder.Services.AddRateLimiter(opts =>
    {
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

        opts.AddPolicy("checkout", http =>
        {
            var xff = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            var partitionKey = !string.IsNullOrWhiteSpace(xff)
                ? xff.Split(',')[0].Trim()
                : (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: partitionKey,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });

        opts.RejectionStatusCode = 429;
    });

    builder.Services.AddCors(opts =>
    {
        opts.AddPolicy("landing", policy =>
        {
            policy.WithOrigins(
                    "https://limitlesscopier.com",
                    "https://www.limitlesscopier.com",
                    "http://localhost:3000",
                    "http://localhost:5500")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // =========================================================
    // BUILD + PIPELINE
    // =========================================================
    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseRateLimiter();
    app.UseCors("landing");

    // Auto-create / migrate DB on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        db.Database.EnsureCreated();
        Log.Information("Database ready at {Path}",
            app.Configuration["Database:Path"] ?? "licenses.db");

        // === DASHBOARD: BEGIN ===
        var schemaLog = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("SchemaUpgrade");
        await SchemaUpgrade.RunAsync(db, schemaLog).ConfigureAwait(false);

        // Backfill: any Licenses without a matching Affiliate row get one.
        // This catches licenses that were minted BEFORE the dashboard build
        // shipped. Runs once per startup; cheap because it's keyed on a
        // left-join filter, not a full table scan in the steady state.
        var backfillLog = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("AffiliateBackfill");

        var licensesNeedingAffiliate = await db.Licenses
            .Where(l => !db.Affiliates.Any(a => a.LicenseId == l.Id))
            .Select(l => l.Id)
            .ToListAsync().ConfigureAwait(false);

        if (licensesNeedingAffiliate.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var id in licensesNeedingAffiliate)
            {
                db.Affiliates.Add(new Affiliate
                {
                    LicenseId = id,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
            backfillLog.LogInformation("Backfilled {Count} affiliate rows for pre-existing licenses",
                licensesNeedingAffiliate.Count);
        }
        else
        {
            backfillLog.LogInformation("Affiliate backfill: nothing to do");
        }
        // === DASHBOARD: END ===
    }

    // =========================================================
    // ENDPOINTS
    // =========================================================
    var customerGroup = app.MapGroup("").RequireRateLimiting("customer");
    customerGroup.MapCustomerEndpoints();

    app.MapGet("/health", () => Results.Ok(new {
        status = "ok",
        time   = DateTime.UtcNow
    }));

    app.MapAdminEndpoints();

    // === NOWPAY: BEGIN ===
    var checkoutGroup = app.MapGroup("").RequireRateLimiting("checkout");
    checkoutGroup.MapCheckoutEndpoints();
    app.MapNowPaymentsWebhook();
    // === NOWPAY: END ===

    // === DASHBOARD: BEGIN ===
    // Dashboard endpoints inherit the "checkout" rate limit (10/min/IP).
    // Same limit applies whether the caller is paying or viewing — both
    // are knowledge-of-key gated and we want a uniform anti-abuse cap.
    var dashboardGroup = app.MapGroup("").RequireRateLimiting("checkout");
    dashboardGroup.MapDashboardEndpoints();
    // === DASHBOARD: END ===

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
