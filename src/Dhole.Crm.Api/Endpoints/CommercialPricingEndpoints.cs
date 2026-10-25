using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record LinkOpportunityQuoteRequest(Guid PricingQuoteId,Guid? PricingRequestId);
public sealed record LinkOpportunityRequestRequest(Guid PricingRequestId);
public sealed record CreateOpportunityRateRequest(
    string Priority,string? ShipmentMode,string? OriginName,string? DestinationName,
    Guid? PoeId,string? PoeName,Guid? PodId,string? PodName,JsonElement Payload);

internal static class CommercialPricingEndpoints
{
    private static readonly HashSet<string> QuoteStatuses = [
       "PendingApproval","ApprovedByManagement","RejectedByManagement","Open",
       "Sent","AcceptedByClient","RejectedByClient","RequestedByClient",
       "Closed","Expired","Cancelled"
    ];
    public static RouteGroupBuilder MapCommercialPricing(this RouteGroupBuilder g)
    {
        g.MapGet("/opportunities/{id:guid}/quotes",ListQuotes);
        g.MapPost("/opportunities/{id:guid}/quotes",LinkQuote);
        g.MapPost("/opportunities/{id:guid}/quotes/{quoteId:guid}/refresh",RefreshQuote);
        g.MapGet("/opportunities/{id:guid}/rate-requests",ListRequests);
        g.MapPost("/opportunities/{id:guid}/rate-requests",CreateRequest);
        g.MapPost("/opportunities/{id:guid}/rate-requests/link",LinkRequest);
        g.MapPost("/opportunities/{id:guid}/rate-requests/{requestId:guid}/refresh",RefreshRequest);
        return g;
    }
    private static async Task<Opportunity?> Allowed(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var o=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        return o is not null && CommercialAccess.OpportunityOwns(h,o)?o:null;
    }
    private static bool Read(HttpContext h) =>
        CrmAccess.Scope(h,"crm.quote.read") && CrmDataEndpoints.HasRead(h,"opportunity");
    private static bool Link(HttpContext h) =>
        CrmAccess.Scope(h,"crm.quote.link") && CrmAccess.Scope(h,"crm.opportunity.update");
    private static bool CanAssociate(HttpContext h,Guid executive,Guid? pricingSeller) =>
        (pricingSeller.HasValue && pricingSeller.Value==executive) ||
        CrmAccess.Scope(h,"crm.opportunity.read-all");
    private static string? Bearer(HttpContext h)
    {
        var value=h.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?
            value["Bearer ".Length..].Trim():null;
    }
    private static IResult PricingError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound=>Results.NotFound(),
        HttpStatusCode.Unauthorized=>Results.Unauthorized(),
        HttpStatusCode.Forbidden=>CrmAccess.Denied(),
        HttpStatusCode.Conflict=>CrmDataEndpoints.Conflict("Pricing rejected a conflicting or duplicate request"),
        HttpStatusCode.BadRequest=>CrmDataEndpoints.Bad("Pricing refused invalid commercial input"),
        _=>Results.Problem(statusCode:503,title:"Pricing dependency unavailable")
    };
    private static IResult FailureIfAny<T>(PricingResult<T> result) =>
        PricingError(result.Status);
    private static bool ValidQuote(PricingQuoteSnapshot q) =>
        q.Id!=Guid.Empty && QuoteStatuses.Contains(q.Status) &&
        !string.IsNullOrWhiteSpace(q.QuoteNumber) && q.QuoteNumber.Length<=100 &&
        !string.IsNullOrWhiteSpace(q.ShipmentMode) && q.ShipmentMode.Length<=60;
    private static async Task<IResult> ListQuotes(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        if(await Allowed(id,db,h,ct) is null)return Results.NotFound();
        return Results.Ok(await db.OpportunityQuotes.AsNoTracking().Where(x=>x.OpportunityId==id)
            .OrderByDescending(x=>x.LinkedAt).ToListAsync(ct));
    }
    private static async Task<IResult> ListRequests(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        if(await Allowed(id,db,h,ct) is null)return Results.NotFound();
        return Results.Ok(await db.OpportunityPricingRequests.AsNoTracking().Where(x=>x.OpportunityId==id)
            .OrderByDescending(x=>x.LinkedAt).ToListAsync(ct));
    }
    private static async Task<IResult> LinkQuote(Guid id,LinkOpportunityQuoteRequest r,
        CrmDbContext db,HttpContext h,IPricingCommercialClient pricing,CancellationToken ct)
    {
        if(!Link(h))return CrmAccess.Denied();
        var o=await Allowed(id,db,h,ct);if(o is null)return Results.NotFound();
        if(r.PricingQuoteId==Guid.Empty)return CrmDataEndpoints.Bad("Pricing quote ID required");
        var bearer=Bearer(h);if(bearer is null)return Results.Unauthorized();
        var remote=await pricing.GetQuote(r.PricingQuoteId,bearer,ct);
        if(!remote.Success)return FailureIfAny(remote);
        var quote=remote.Value!;
        if(!ValidQuote(quote))return Results.Problem(statusCode:502,title:"Invalid Pricing quote projection");
        if(!CanAssociate(h,o.ExecutiveUserId,quote.SellerUserId))return CrmAccess.Denied();
        if(r.PricingRequestId is { } reqId &&
            quote.RequestId != reqId)return CrmDataEndpoints.Conflict("Pricing quote belongs to another request");
        var linkedRequestId=r.PricingRequestId??quote.RequestId;
        if(linkedRequestId is { } actualRequest)
        {
            var local=await db.OpportunityPricingRequests.AsNoTracking().FirstOrDefaultAsync(
                x=>x.PricingRequestId==actualRequest,ct);
            if(local is not null && local.OpportunityId!=id)
                return CrmDataEndpoints.Conflict("Pricing request already belongs to another opportunity");
        }
        var previous=await db.OpportunityQuotes.SingleOrDefaultAsync(
            x=>x.OpportunityId==id&&x.PricingQuoteId==r.PricingQuoteId,ct);
        if(previous is not null)
        {
            return Results.Ok(previous);
        }
        var item=new OpportunityQuote {OpportunityId=id,PricingQuoteId=quote.Id,
            PricingRequestId=linkedRequestId,QuoteNumberSnapshot=quote.QuoteNumber,
            CommercialStatusSnapshot=quote.Status,ShipmentModeSnapshot=quote.ShipmentMode,
            CreatedByUserId=CrmAccess.UserId(h)};
        db.OpportunityQuotes.Add(item);
        try {await db.SaveChangesAsync(ct);
            return Results.Created("/api/crm/opportunities/"+id+"/quotes",item);}
        catch(DbUpdateException){return CrmDataEndpoints.Conflict("Quote link already exists");}
    }
    private static async Task<IResult> RefreshQuote(Guid id,Guid quoteId,CrmDbContext db,
        HttpContext h,IPricingCommercialClient pricing,CancellationToken ct)
    {
        if(!Link(h))return CrmAccess.Denied();
        var o=await Allowed(id,db,h,ct);if(o is null)return Results.NotFound();
        var local=await db.OpportunityQuotes.FirstOrDefaultAsync(
            x=>x.OpportunityId==id&&x.PricingQuoteId==quoteId,ct);
        if(local is null)return Results.NotFound();
        var bearer=Bearer(h);if(bearer is null)return Results.Unauthorized();
        var remote=await pricing.GetQuote(quoteId,bearer,ct);
        if(!remote.Success)return FailureIfAny(remote);
        var q=remote.Value!;
        if(!ValidQuote(q)||q.Id!=quoteId)return Results.Problem(statusCode:502,title:"Invalid Pricing quote");
        if(!CanAssociate(h,o.ExecutiveUserId,q.SellerUserId))return CrmAccess.Denied();
        if(local.PricingRequestId is { } requestId && q.RequestId!=requestId)
            return CrmDataEndpoints.Conflict("Quote request ownership changed in Pricing");
        local.CommercialStatusSnapshot=q.Status;local.QuoteNumberSnapshot=q.QuoteNumber;
        local.ShipmentModeSnapshot=q.ShipmentMode;local.LastSyncAt=DateTime.UtcNow;local.PendingRefreshAt=null;
        local.UpdatedAt=DateTime.UtcNow;local.UpdatedByUserId=CrmAccess.UserId(h);local.RowVersion++;
        var saved=await CrmDataEndpoints.Commit(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(local):saved;
    }
    private static async Task<IResult> LinkRequest(Guid id,LinkOpportunityRequestRequest r,
        CrmDbContext db,HttpContext h,IPricingCommercialClient pricing,CancellationToken ct)
    {
        if(!Link(h))return CrmAccess.Denied();
        var o=await Allowed(id,db,h,ct);if(o is null)return Results.NotFound();
        if(r.PricingRequestId==Guid.Empty)return CrmDataEndpoints.Bad("Request ID required");
        var bearer=Bearer(h);if(bearer is null)return Results.Unauthorized();
        var remote=await pricing.GetRequest(r.PricingRequestId,bearer,ct);
        if(!remote.Success)return FailureIfAny(remote);
        var data=remote.Value!;
        if(data.Id!=r.PricingRequestId || !CanAssociate(h,o.ExecutiveUserId,data.SellerUserId))
            return CrmAccess.Denied();
        var existing=await db.OpportunityPricingRequests.AsNoTracking().FirstOrDefaultAsync(
            x=>x.PricingRequestId==r.PricingRequestId,ct);
        if(existing is not null)
            return existing.OpportunityId==id?Results.Ok(existing):
                CrmDataEndpoints.Conflict("Rate request is already linked to another opportunity");
        var item=new OpportunityPricingRequest {OpportunityId=id,PricingRequestId=data.Id,
            PricingQuoteId=data.RateId,SellerUserId=data.SellerUserId??o.ExecutiveUserId,
            StatusSnapshot=data.Status,CreatedByUserId=CrmAccess.UserId(h)};
        db.OpportunityPricingRequests.Add(item);
        try {await db.SaveChangesAsync(ct);return Results.Created(
            "/api/crm/opportunities/"+id+"/rate-requests",item);}
        catch(DbUpdateException){return CrmDataEndpoints.Conflict("Concurrent Pricing request link");}
    }
    private static async Task<IResult> CreateRequest(Guid id,CreateOpportunityRateRequest r,
        CrmDbContext db,HttpContext h,IPricingCommercialClient pricing,CancellationToken ct)
    {
        if(!Link(h))return CrmAccess.Denied();
        var o=await Allowed(id,db,h,ct);if(o is null)return Results.NotFound();
        if(o.Status!="Open")return CrmDataEndpoints.Conflict("Opportunity must be open");
        var rawKey=h.Request.Headers["Idempotency-Key"].ToString();
        if(rawKey.Length is < 8 or > 128 ||
            rawKey.Any(x=>!char.IsAsciiLetterOrDigit(x)&&x is not ('-' or '_' or '.' or ':')))
            return CrmDataEndpoints.Bad("A stable Idempotency-Key (8 to 128 safe characters) is required");
        if(r.Priority is not ("Green" or "Yellow" or "Red")||
           r.Payload.ValueKind!=JsonValueKind.Object)
            return CrmDataEndpoints.Bad("Invalid Pricing priority or request payload");
        var bearer=Bearer(h);if(bearer is null)return Results.Unauthorized();
        string clientName;
        if(o.AccountId is { } acc)
        {
            var accName=await db.Accounts.Where(x=>x.Id==acc)
                .Select(x=>x.DisplayName).FirstOrDefaultAsync(ct);
            if(string.IsNullOrWhiteSpace(accName))return Results.NotFound();
            clientName=accName;
        }
        else
        {
            var lead=await db.Leads.Where(x=>x.Id==o.LeadId)
                .Select(x=>new {x.Name,x.CompanyName}).FirstOrDefaultAsync(ct);
            if(lead is null)return Results.NotFound();
            clientName=lead.CompanyName??lead.Name;
        }
        var create=new PricingCreateRequest(r.Priority,clientName,null,r.ShipmentMode,
            r.OriginName,r.DestinationName,r.PoeId,r.PoeName,r.PodId,r.PodName,
            o.ExecutiveUserId,r.Payload);
        var payloadHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(create))));
        var keyHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
        var prior=await db.OpportunityPricingRequests.AsNoTracking().FirstOrDefaultAsync(
            x=>x.OpportunityId==id&&x.IdempotencyKeyHash==keyHash,ct);
        if(prior is not null)
            return prior.RequestPayloadHash==payloadHash?Results.Ok(prior):
                CrmDataEndpoints.Conflict("Idempotency-Key reused with different payload");
        var result=await pricing.CreateRequest(create,bearer,"crm-"+id.ToString("N")+"-"+rawKey,
            h.Request.Headers["X-Correlation-Id"].ToString() is {Length:>0} corr?
                corr:h.TraceIdentifier,ct);
        if(!result.Success)return FailureIfAny(result);
        if(result.Value==Guid.Empty)return Results.Problem(statusCode:502,title:"Pricing returned an invalid request ID");
        var actual=await pricing.GetRequest(result.Value,bearer,ct);
        if(!actual.Success)return FailureIfAny(actual);
        var data=actual.Value!;
        if(data.Id!=result.Value||data.SellerUserId!=o.ExecutiveUserId)
            return CrmAccess.Denied();
        var record=new OpportunityPricingRequest {OpportunityId=id,PricingRequestId=data.Id,
            PricingQuoteId=data.RateId,SellerUserId=o.ExecutiveUserId,StatusSnapshot=data.Status,
            IdempotencyKeyHash=keyHash,RequestPayloadHash=payloadHash,CreatedByUserId=CrmAccess.UserId(h)};
        db.OpportunityPricingRequests.Add(record);
        try {await db.SaveChangesAsync(ct);return Results.Created(
            "/api/crm/opportunities/"+id+"/rate-requests",record);}
        catch(DbUpdateException)
        {
            var existed=await db.OpportunityPricingRequests.AsNoTracking().FirstOrDefaultAsync(
                x=>x.PricingRequestId==data.Id,ct);
            return existed is not null&&existed.OpportunityId==id&&existed.RequestPayloadHash==payloadHash?
                Results.Ok(existed):CrmDataEndpoints.Conflict("Request created in Pricing; retry with the SAME Idempotency-Key to reconcile");
        }
    }
    private static async Task<IResult> RefreshRequest(Guid id,Guid requestId,CrmDbContext db,
        HttpContext h,IPricingCommercialClient pricing,CancellationToken ct)
    {
        if(!Link(h))return CrmAccess.Denied();
        var o=await Allowed(id,db,h,ct);if(o is null)return Results.NotFound();
        var record=await db.OpportunityPricingRequests.FirstOrDefaultAsync(
            x=>x.OpportunityId==id&&x.PricingRequestId==requestId,ct);
        if(record is null)return Results.NotFound();
        var bearer=Bearer(h);if(bearer is null)return Results.Unauthorized();
        var remote=await pricing.GetRequest(requestId,bearer,ct);
        if(!remote.Success)return FailureIfAny(remote);
        var rr=remote.Value!;
        if(rr.Id!=requestId||!CanAssociate(h,o.ExecutiveUserId,rr.SellerUserId))
            return CrmAccess.Denied();
        if(record.PricingQuoteId is { } old && rr.RateId is { } current && old!=current)
            return CrmDataEndpoints.Conflict("Pricing request changed associated quote");
        if(rr.RateId is { } rateId)
        {
            var quote=await pricing.GetQuote(rateId,bearer,ct);
            if(!quote.Success)return FailureIfAny(quote);
            var q=quote.Value!;
            if(!ValidQuote(q)||q.Id!=rateId||q.RequestId!=requestId||
               !CanAssociate(h,o.ExecutiveUserId,q.SellerUserId))
                return CrmDataEndpoints.Conflict("Pricing quote mismatch");
            var local=await db.OpportunityQuotes.FirstOrDefaultAsync(
                x=>x.OpportunityId==id&&x.PricingQuoteId==rateId,ct);
            if(local is null) db.OpportunityQuotes.Add(new OpportunityQuote {
                OpportunityId=id,PricingQuoteId=rateId,PricingRequestId=requestId,
                QuoteNumberSnapshot=q.QuoteNumber,ShipmentModeSnapshot=q.ShipmentMode,
                CommercialStatusSnapshot=q.Status,CreatedByUserId=CrmAccess.UserId(h)
            });
            else
            {
                local.QuoteNumberSnapshot=q.QuoteNumber;
                local.CommercialStatusSnapshot=q.Status;local.ShipmentModeSnapshot=q.ShipmentMode;
                local.LastSyncAt=DateTime.UtcNow;local.PendingRefreshAt=null;local.RowVersion++;
                local.UpdatedAt=DateTime.UtcNow;local.UpdatedByUserId=CrmAccess.UserId(h);
            }
        }
        record.StatusSnapshot=rr.Status;record.PricingQuoteId=rr.RateId;
        record.LastSyncAt=DateTime.UtcNow;record.PendingRefreshAt=null;record.RowVersion++;
        record.UpdatedAt=DateTime.UtcNow;record.UpdatedByUserId=CrmAccess.UserId(h);
        var saved=await CrmDataEndpoints.Commit(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(record):saved;
    }
}
