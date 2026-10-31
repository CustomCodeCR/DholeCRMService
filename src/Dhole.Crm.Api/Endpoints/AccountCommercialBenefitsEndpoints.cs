using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record AccountBenefitSettingsRequest(
    long? ExpectedVersion, string? MiamiLclPlanCode, string? Notes);
public sealed record AccountPreferentialRateRequest(
    long? ExpectedVersion, string Modality, string? LaneCode, string ChargeCode,
    string? CustomChargeName, string RateType, decimal? Value, decimal? MinimumCharge,
    string? CurrencyCode, string? UnitCode, string? EquipmentCode,
    string? OriginCode, string? DestinationCode,
    DateTime? ValidFrom, DateTime? ValidUntil, string? Notes, bool IsActive);

internal static class AccountCommercialBenefitsEndpoints
{
    public static RouteGroupBuilder MapAccountCommercialBenefits(this RouteGroupBuilder group)
    {
        group.MapGet("/accounts/{accountId:guid}/commercial-benefits", Get);
        group.MapPut("/accounts/{accountId:guid}/commercial-benefits/settings", SaveSettings);
        group.MapPost("/accounts/{accountId:guid}/commercial-benefits/rates", CreateRate);
        group.MapPut("/accounts/{accountId:guid}/commercial-benefits/rates/{rateId:guid}", UpdateRate);
        return group;
    }

    private static async Task<bool> Visible(Guid accountId, CrmDbContext db, HttpContext http,
        CancellationToken ct) => await CrmAccess.CanSeeAccount(db, http, accountId, ct);

    private static bool CanRead(HttpContext http) => CrmDataEndpoints.HasRead(http, "account");
    private static bool CanManage(HttpContext http) => CrmAccess.Scope(http, "crm.account.update");

    private static async Task<IResult> Get(Guid accountId, CrmDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (!CanRead(http)) return CrmAccess.Denied();
        if (!await Visible(accountId, db, http, ct)) return Results.NotFound();
        var settings = await db.AccountBenefitSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
        var rates = await db.AccountPreferentialRates.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderBy(x => x.Modality).ThenBy(x => x.ChargeCode)
            .ThenByDescending(x => x.IsActive).ThenByDescending(x => x.UpdatedAt)
            .ToArrayAsync(ct);
        return Results.Ok(new
        {
            accountId,
            settings,
            rates,
            pricingIntegrationStatus = "NOT_CONNECTED",
            note = "CRM-only negotiated benefits; Pricing must not apply them until explicitly integrated."
        });
    }

    private static async Task<IResult> SaveSettings(Guid accountId,
        AccountBenefitSettingsRequest request, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CanManage(http)) return CrmAccess.Denied();
        if (!await Visible(accountId, db, http, ct)) return Results.NotFound();
        var plan = Clean(request.MiamiLclPlanCode);
        var notes = Clean(request.Notes);
        if (!CommercialBenefitRules.ValidMiamiPlan(plan) || notes?.Length > 2000)
            return CrmDataEndpoints.Bad("Invalid LCL Miami category or notes");
        var settings = await db.AccountBenefitSettings
            .SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
        var actor = CrmAccess.UserId(http);
        if (settings is null)
        {
            if (request.ExpectedVersion.HasValue)
                return CrmDataEndpoints.Conflict("Account benefits changed; reload before saving");
            settings = new AccountBenefitSettings { AccountId = accountId, CreatedByUserId = actor };
            db.AccountBenefitSettings.Add(settings);
        }
        else
        {
            if (request.ExpectedVersion != settings.RowVersion)
                return CrmDataEndpoints.Conflict("Account benefits changed; reload before saving");
            settings.RowVersion++;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedByUserId = actor;
        }
        settings.MiamiLclPlanCode = plan;
        settings.Notes = notes;
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(settings) : result;
    }

    private static async Task<IResult> CreateRate(Guid accountId,
        AccountPreferentialRateRequest request, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CanManage(http)) return CrmAccess.Denied();
        if (!await Visible(accountId, db, http, ct)) return Results.NotFound();
        if (request.ExpectedVersion.HasValue)
            return CrmDataEndpoints.Bad("New benefit cannot have an existing version");
        var actor = CrmAccess.UserId(http);
        var rate = new AccountPreferentialRate { AccountId = accountId, CreatedByUserId = actor };
        Apply(rate, request);
        if (!CommercialBenefitRules.ValidRate(rate))
            return CrmDataEndpoints.Bad("Invalid modality, charge, financial value, unit or validity");
        if (await Conflicts(db, rate, Guid.Empty, ct))
            return CrmDataEndpoints.Conflict("Another active benefit has the same scope and overlapping dates");
        db.AccountPreferentialRates.Add(rate);
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ?
            Results.Created($"/api/crm/accounts/{accountId}/commercial-benefits/rates/{rate.Id}", rate) : result;
    }

    private static async Task<IResult> UpdateRate(Guid accountId, Guid rateId,
        AccountPreferentialRateRequest request, CrmDbContext db, HttpContext http,
        CancellationToken ct)
    {
        if (!CanManage(http)) return CrmAccess.Denied();
        if (!await Visible(accountId, db, http, ct)) return Results.NotFound();
        var rate = await db.AccountPreferentialRates.SingleOrDefaultAsync(x =>
            x.Id == rateId && x.AccountId == accountId, ct);
        if (rate is null) return Results.NotFound();
        if (!request.ExpectedVersion.HasValue || rate.RowVersion != request.ExpectedVersion.Value)
            return CrmDataEndpoints.Conflict("Preferential benefit was modified; reload before editing");
        var candidate = new AccountPreferentialRate { AccountId = accountId };
        Apply(candidate, request);
        if (!CommercialBenefitRules.ValidRate(candidate))
            return CrmDataEndpoints.Bad("Invalid modality, charge, financial value, unit or validity");
        if (await Conflicts(db, candidate, rateId, ct))
            return CrmDataEndpoints.Conflict("Another active benefit has the same scope and overlapping dates");
        Apply(rate, request);
        rate.RowVersion++;
        rate.UpdatedAt = DateTime.UtcNow;
        rate.UpdatedByUserId = CrmAccess.UserId(http);
        var result = await CrmDataEndpoints.Commit(db, ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(rate) : result;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Apply(AccountPreferentialRate record, AccountPreferentialRateRequest r)
    {
        record.Modality = r.Modality?.Trim().ToUpperInvariant() ?? "";
        record.LaneCode = Clean(r.LaneCode)?.ToUpperInvariant();
        record.ChargeCode = r.ChargeCode?.Trim().ToUpperInvariant() ?? "";
        record.CustomChargeName = record.ChargeCode == "OTHER" ? Clean(r.CustomChargeName) : null;
        record.RateType = r.RateType?.Trim().ToUpperInvariant() ?? "";
        record.Value = r.Value;
        record.MinimumCharge = r.MinimumCharge;
        record.CurrencyCode = Clean(r.CurrencyCode)?.ToUpperInvariant();
        record.UnitCode = Clean(r.UnitCode)?.ToUpperInvariant();
        record.EquipmentCode = Clean(r.EquipmentCode)?.ToUpperInvariant();
        record.OriginCode = Clean(r.OriginCode)?.ToUpperInvariant();
        record.DestinationCode = Clean(r.DestinationCode)?.ToUpperInvariant();
        record.ValidFrom = r.ValidFrom;
        record.ValidUntil = r.ValidUntil;
        record.Notes = Clean(r.Notes);
        record.IsActive = r.IsActive;
    }

    private static async Task<bool> Conflicts(CrmDbContext db, AccountPreferentialRate candidate,
        Guid excludedId, CancellationToken ct)
    {
        if (!candidate.IsActive) return false;
        var existing = await db.AccountPreferentialRates.AsNoTracking()
            .Where(x => x.AccountId == candidate.AccountId && x.IsActive &&
                x.Id != excludedId && x.Modality == candidate.Modality &&
                x.ChargeCode == candidate.ChargeCode)
            .ToListAsync(ct);
        return existing.Any(x => CommercialBenefitRules.Overlap(x, candidate));
    }
}
