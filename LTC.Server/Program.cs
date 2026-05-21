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

    // === NOWPAY: BEGIN - new services for crypto checkout ===
    // Configuration binding: env vars NowPayments__ApiKey, NowPayments__IpnSecret
    builder.Services.Configure<NowPaymentsOptions>(
        builder.Configuration.GetSection("NowPayments"));
    // Configuration binding: env vars Resend__ApiKey, Resend__FromEmail, etc.
    builder.Services.Configure<ResendOptions>(
        builder.Configuration.GetSection("Resend"));

    // HttpClient-backed services — registered as typed HttpClient consumers
    // so .NET handles pooling + DNS refresh correctly.
    builder.Services.AddHttpClient<NowPaymentsClient>();
    builder.Services.AddHttpClient<EmailService>();

    // Scoped services that share the DbContext
    builder.Services.AddScoped<OrderService>();
    builder.Services.AddScoped<LicensingService>();
    // === NOWPAY: END ===

    // Rate limiting — built into ASP.NET Core 8. Each policy is
    // attached to specific endpoints below.
    builder.Services.AddRateLimiter(opts =>
    {
        // "customer" policy — applies to /activate, /heartbeat, /deactivate
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

        // === NOWPAY: BEGIN - rate limit for checkout creation ===
        // "checkout" policy - 10 invoices per minute per IP. Anti-abuse.
        // Webhook endpoint is NOT rate-limited (NowPayments controls retry).
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
        // === NOWPAY: END ===

        opts.RejectionStatusCode = 429;
    });

    // === NOWPAY: BEGIN - CORS for landing page calls ===
    // The landing page at https://limitlesscopier.com calls /api/checkout/create
    // and /api/checkout/status/* directly from the browser. Allow it.
    builder.Services.AddCors(opts =>
    {
        opts.AddPolicy("landing", policy =>
        {
            policy.WithOrigins(
                    "https://limitlesscopier.com",
                    "https://www.limitlesscopier.com",
                    "http://localhost:3000",      // local dev
                    "http://localhost:5500")      // local dev
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
    // === NOWPAY: END ===

    // =========================================================
    // BUILD + PIPELINE
    // =========================================================
    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseRateLimiter();

    // === NOWPAY: BEGIN ===
    app.UseCors("landing");
    // === NOWPAY: END ===

    // Auto-create / migrate DB on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        db.Database.EnsureCreated();
        Log.Information("Database ready at {Path}",
            app.Configuration["Database:Path"] ?? "licenses.db");

        // === DASHBOARD: BEGIN ===
        // EnsureCreated() does NOT add new tables to an EXISTING database.
        // We need to add the 4 new tables (Affiliates, DiscountCodes,
        // CodeRedemptions, Commissions) via raw CREATE TABLE IF NOT EXISTS
        // SQL so existing deploys (like production) get the new tables on
        // their next restart. See Services/SchemaUpgrade.cs for the SQL.
        // Idempotent — safe to run on every startup.
        var schemaLog = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("SchemaUpgrade");
        await SchemaUpgrade.RunAsync(db, schemaLog).ConfigureAwait(false);
        // === DASHBOARD: END ===
    }

    // =========================================================
    // ENDPOINTS
    // =========================================================
    var customerGroup = app.MapGroup("").RequireRateLimiting("customer");
    customerGroup.MapCustomerEndpoints();

    // Health check
    app.MapGet("/health", () => Results.Ok(new {
        status = "ok",
        time   = DateTime.UtcNow
    }));

    app.MapAdminEndpoints();   // bearer-protected; sets up its own group

    // === NOWPAY: BEGIN - new endpoints ===
    // Checkout creation has its own rate limit (10/min per IP).
    var checkoutGroup = app.MapGroup("").RequireRateLimiting("checkout");
    checkoutGroup.MapCheckoutEndpoints();

    // Webhook is NOT rate-limited — NowPayments controls retry behavior.
    // Signature verification provides authentication.
    app.MapNowPaymentsWebhook();
    // === NOWPAY: END ===

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
