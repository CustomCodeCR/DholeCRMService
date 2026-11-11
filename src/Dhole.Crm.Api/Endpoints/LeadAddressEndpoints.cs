using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.Realtime;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record LeadAddressRequest(string AddressType, string AddressText,
    Guid? CountryId = null, string? CountryCode = null, string? Location = null,
    string? Admin1Code = null, string? Admin1Name = null, string? Admin2Code = null,
    string? Admin2Name = null, string? Admin3Code = null, string? Admin3Name = null,
    string? PostalCode = null, decimal? LocationLatitude = null, decimal? LocationLongitude = null);
public sealed record LeadAddressPatchRequest(long ExpectedVersion, string AddressType, string AddressText,
    string? Location = null, decimal? LocationLatitude = null, decimal? LocationLongitude = null);

internal static class LeadAddressEndpoints
{
    public static RouteGroupBuilder MapLeadAddresses(this RouteGroupBuilder group)
    {
        group.MapGet("/leads/{id:guid}/addresses", List);
        group.MapPost("/leads/{id:guid}/addresses", Create);
        group.MapPatch("/lead-addresses/{id:guid}", Patch);
        return group;
    }
    private static async Task<Lead?> VisibleLead(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        return lead is not null && CrmAccess.LeadAccess(h, lead.ExecutiveUserId) ? lead : null;
    }
    private static async Task<IResult> List(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CrmDataEndpoints.HasRead(h,"lead")) return CrmAccess.Denied();
        if (await VisibleLead(id,db,h,ct) is null) return CrmAccess.Denied();
        return Results.Ok(await db.LeadAddresses.AsNoTracking().Where(a => a.LeadId == id)
            .OrderBy(a => a.AddressType).ThenBy(a => a.AddressText).ToListAsync(ct));
    }
    private static async Task<IResult> Create(Guid id, LeadAddressRequest r, CrmDbContext db,
        HttpContext h, IConfiguration flags, IConfigReferenceService config, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h,"crm.lead.update")) return CrmAccess.Denied();
        var lead = await VisibleLead(id,db,h,ct);
        if (lead is null) return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be edited");
        if (!LeadLocationRules.IsValid(r.AddressText,r.AddressType,r.LocationLatitude,r.LocationLongitude) ||
            r.CountryCode is { Length: > 2 } || r.Location is { Length: > 2000 } ||
            new[] {r.Admin1Code,r.Admin1Name,r.Admin2Code,r.Admin2Name,r.Admin3Code,r.Admin3Name}.Any(x => x is { Length: > 160 }) ||
            r.PostalCode is { Length: > 32 })
            return CrmDataEndpoints.Bad("Invalid prospect address");
        if (!await CrmDataEndpoints.ValidCountry(config,r.CountryId,ct))
            return CrmDataEndpoints.Bad("Unknown country");
        var address = new LeadAddress
        {
            LeadId=id, AddressType=r.AddressType.Trim(), AddressText=r.AddressText.Trim(),
            CountryId=r.CountryId??lead.CountryId, CountryCode=r.CountryCode?.Trim(),
            Location=r.Location?.Trim(), Admin1Code=r.Admin1Code?.Trim(),
            Admin1Name=r.Admin1Name?.Trim(), Admin2Code=r.Admin2Code?.Trim(),
            Admin2Name=r.Admin2Name?.Trim(), Admin3Code=r.Admin3Code?.Trim(),
            Admin3Name=r.Admin3Name?.Trim(), PostalCode=r.PostalCode?.Trim(),
            LocationLatitude=r.LocationLatitude, LocationLongitude=r.LocationLongitude,
            CreatedByUserId=CrmAccess.UserId(h)
        };
        db.LeadAddresses.Add(address);
        lead.RowVersion++;
        lead.UpdatedAt=DateTime.UtcNow;
        lead.UpdatedByUserId=CrmAccess.UserId(h);
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,flags,h.TraceIdentifier,ct);
            return Results.Created("/api/crm/leads/"+id+"/addresses",address);
        }
        catch (DbUpdateConcurrencyException) { return CrmDataEndpoints.Conflict("Record modified concurrently"); }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Invalid prospect address"); }
    }
    private static async Task<IResult> Patch(Guid id, LeadAddressPatchRequest r, CrmDbContext db,
        HttpContext h, IConfiguration flags, CancellationToken ct)
    {
        if (!CrmAccess.Scope(h,"crm.lead.update")) return CrmAccess.Denied();
        var address = await db.LeadAddresses.FirstOrDefaultAsync(a => a.Id == id,ct);
        if (address is null) return Results.NotFound();
        var lead=await VisibleLead(address.LeadId,db,h,ct);
        if (lead is null) return CrmAccess.Denied();
        if (lead.ConvertedAt is not null) return CrmDataEndpoints.Conflict("Converted lead cannot be edited");
        if (address.RowVersion != r.ExpectedVersion)
            return CrmDataEndpoints.Conflict("Stale prospect address");
        if (!LeadLocationRules.IsValid(r.AddressText,r.AddressType,r.LocationLatitude,r.LocationLongitude) ||
            r.Location is { Length: > 2000 }) return CrmDataEndpoints.Bad("Invalid prospect address");
        address.AddressType=r.AddressType.Trim();
        address.AddressText=r.AddressText.Trim();
        address.Location=r.Location?.Trim();
        address.LocationLatitude=r.LocationLatitude;
        address.LocationLongitude=r.LocationLongitude;
        address.RowVersion++;
        address.UpdatedAt=DateTime.UtcNow;
        address.UpdatedByUserId=CrmAccess.UserId(h);
        lead.RowVersion++;
        lead.UpdatedAt=DateTime.UtcNow;
        lead.UpdatedByUserId=CrmAccess.UserId(h);
        try
        {
            await CrmCollectionRealtimeCommit.SaveLeadAsync(db,flags,h.TraceIdentifier,ct);
            return Results.Ok(address);
        }
        catch (DbUpdateConcurrencyException) { return CrmDataEndpoints.Conflict("Record modified concurrently"); }
        catch (DbUpdateException) { return CrmDataEndpoints.Conflict("Invalid prospect address"); }
    }
}
