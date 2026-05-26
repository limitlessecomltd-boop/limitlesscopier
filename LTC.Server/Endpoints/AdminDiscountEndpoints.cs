using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LTC.Server.Data;
using LTC.Server.Models;

namespace LTC.Server.Endpoints;

/// <summary>
/// Admin CRUD for discount codes, plus a read-only commissions overview.
/// Bearer-protected with the same static token + fixed-time comparison as
/// AdminEndpoints. Kept in its own file/group so the discount-code surface
/// is self-contained, but the auth model is identical.
///
/// Routes (all under /admin, all require Bearer token):
///   GET    /admin/discounts                 - list all discount codes
///   POST   /admin/discounts                 - create a code
///   POST   /admin/discounts/{id}/update     - edit fields / enable / disable
///   GET    /admin/commissions               - list commissions (payout overview)
///   POST   /admin/commissions/{id}/mark-paid - mark a commission paid
/// </summary>
public static class AdminDiscountEndpoints
{
    public static void MapAdminDiscountEndpoints(this WebApplication app)
    {
        var token = app.Configuration["Admin:BearerToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            // AdminEndpoints already logs the disabled warning; stay quiet here.
            return;
        }

        var grp = app.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var auth = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault() ?? "";
            if (!auth.StartsWith("Bearer "))
                return Results.Unauthorized();
            var supplied = auth["Bearer ".Length..].Trim();
            if (!CryptographicEquals(supplied, token!))
                return Results.Unauthorized();
            return await next(ctx);
        });

        grp.MapGet ("/discounts",                 ListDiscounts);
        grp.MapPost("/discounts",                 CreateDiscount);
        grp.MapPost("/discounts/{id}/update",     UpdateDiscount);
        grp.MapGet ("/commissions",               ListCommissions);
        grp.MapPost("/commissions/{id}/mark-paid", MarkCommissionPaid);
    }

    // ===================== DISCOUNTS =====================

    private static async Task<IResult> ListDiscounts(LicensingDbContext db)
    {
        var rows = await db.DiscountCodes
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.Code,
                d.DiscountPercent,
                d.DiscountFlatUsd,
                d.MaxUses,
                d.UsedCount,
                d.ExpiresAt,
                d.MinPurchaseUsd,
                d.Enabled,
                d.CreatedAt,
                d.Notes,
            })
            .ToListAsync();
        return Results.Ok(rows);
    }

    public class CreateDiscountRequest
    {
        public string Code { get; set; } = "";
        public int DiscountPercent { get; set; }
        public decimal DiscountFlatUsd { get; set; }
        public int? MaxUses { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public decimal? MinPurchaseUsd { get; set; }
        public string? Notes { get; set; }
    }

    private static async Task<IResult> CreateDiscount(
        [FromBody] CreateDiscountRequest req,
        LicensingDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.BadRequest(new { error = "Code required" });

        var code = req.Code.Trim().ToUpperInvariant();

        // Exactly one of percent / flat must be set.
        var hasPercent = req.DiscountPercent > 0;
        var hasFlat = req.DiscountFlatUsd > 0;
        if (hasPercent == hasFlat)
            return Results.BadRequest(new { error = "Set EITHER a percent OR a flat amount (not both, not neither)." });
        if (hasPercent && (req.DiscountPercent < 1 || req.DiscountPercent > 100))
            return Results.BadRequest(new { error = "Percent must be 1-100." });
        if (hasFlat && req.DiscountFlatUsd < 1)
            return Results.BadRequest(new { error = "Flat amount must be at least $1." });

        // Uniqueness — also enforced by the unique index, but check for a clean error.
        var exists = await db.DiscountCodes.AnyAsync(d => d.Code == code);
        if (exists)
            return Results.BadRequest(new { error = $"Code '{code}' already exists." });

        // Don't collide with an affiliate slug either (they share the code namespace at checkout).
        var affCollision = await db.Affiliates.AnyAsync(a => a.Code == code);
        if (affCollision)
            return Results.BadRequest(new { error = $"'{code}' is already an affiliate code." });

        var dc = new DiscountCode
        {
            Code = code,
            DiscountPercent = hasPercent ? req.DiscountPercent : 0,
            DiscountFlatUsd = hasFlat ? req.DiscountFlatUsd : 0m,
            MaxUses = req.MaxUses,
            UsedCount = 0,
            ExpiresAt = req.ExpiresAt,
            MinPurchaseUsd = req.MinPurchaseUsd,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            Notes = req.Notes,
        };
        db.DiscountCodes.Add(dc);
        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true, id = dc.Id, code = dc.Code });
    }

    public class UpdateDiscountRequest
    {
        // Any field left null is left unchanged. Enabled is nullable so
        // "don't touch" is distinguishable from "set to false".
        public bool? Enabled { get; set; }
        public int? MaxUses { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public decimal? MinPurchaseUsd { get; set; }
        public string? Notes { get; set; }
    }

    private static async Task<IResult> UpdateDiscount(
        string id,
        [FromBody] UpdateDiscountRequest req,
        LicensingDbContext db)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Bad id" });

        var dc = await db.DiscountCodes.FirstOrDefaultAsync(d => d.Id == guid);
        if (dc is null) return Results.NotFound();

        if (req.Enabled is bool en) dc.Enabled = en;
        if (req.MaxUses is int mu) dc.MaxUses = mu;
        if (req.ExpiresAt is DateTime ex) dc.ExpiresAt = ex;
        if (req.MinPurchaseUsd is decimal mp) dc.MinPurchaseUsd = mp;
        if (req.Notes is not null) dc.Notes = req.Notes;

        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true });
    }

    // ===================== COMMISSIONS =====================

    private static async Task<IResult> ListCommissions(LicensingDbContext db)
    {
        // Join commissions to their affiliate's code + license email for the
        // payout view. Newest first.
        var rows = await db.Commissions
            .OrderByDescending(c => c.PaidAt)
            .Take(500)
            .Select(c => new
            {
                c.Id,
                c.OrderId,
                c.Plan,
                c.OrderAmountUsd,
                c.CommissionAmountUsd,
                c.Status,
                c.PaidAt,
                c.EligibleAt,
                c.EarnedAt,
                c.PaidOutAt,
                affiliateCode = db.Affiliates
                    .Where(a => a.Id == c.AffiliateId)
                    .Select(a => a.Code)
                    .FirstOrDefault(),
                affiliateEmail = db.Affiliates
                    .Where(a => a.Id == c.AffiliateId)
                    .Select(a => a.License.Email)
                    .FirstOrDefault(),
            })
            .ToListAsync();
        return Results.Ok(rows);
    }

    public class MarkPaidRequest { public string? Notes { get; set; } }

    private static async Task<IResult> MarkCommissionPaid(
        string id,
        [FromBody] MarkPaidRequest? body,
        LicensingDbContext db)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Bad id" });

        var c = await db.Commissions.FirstOrDefaultAsync(x => x.Id == guid);
        if (c is null) return Results.NotFound();

        // Only "earned" commissions can be paid. Pending ones haven't cleared
        // the 14-day hold yet; cancelled ones are void.
        if (c.Status != "earned")
            return Results.BadRequest(new { error = $"Commission status is '{c.Status}', only 'earned' can be paid." });

        var now = DateTime.UtcNow;
        c.Status = "paid";
        c.PaidOutAt = now;
        if (!string.IsNullOrWhiteSpace(body?.Notes)) c.Notes = body!.Notes;

        // Update the affiliate's denormalized paid total.
        var affiliate = await db.Affiliates.FirstOrDefaultAsync(a => a.Id == c.AffiliateId);
        if (affiliate is not null)
            affiliate.TotalPaidUsd += c.CommissionAmountUsd;

        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true, paidOutAt = now });
    }

    // ===================== HELPERS =====================

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
