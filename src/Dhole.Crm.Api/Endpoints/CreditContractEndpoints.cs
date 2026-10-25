using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreateCreditContractRequest(Guid? CreditProfileId,string? ContractNumber,
    DateTime StartDate,DateTime RenewalDate,DateTime? ExpiryDate,string PaymentTerms,bool AutoRenew);
public sealed record RenewCreditContractRequest(long ExpectedVersion,Guid OperationId,
    DateTime RenewalDate,DateTime? ExpiryDate,string Reason);
public sealed record UpdateContractStatusRequest(long ExpectedVersion,string Reason);
public sealed record AttachSignedContractRequest(long ExpectedVersion,Guid FileId);

internal static class CreditContractEndpoints
{
    public static RouteGroupBuilder MapCreditContracts(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{accountId:guid}/credit-contracts",List);
        g.MapPost("/accounts/{accountId:guid}/credit-contracts",Create);
        g.MapGet("/credit-contracts/{id:guid}",Get);
        g.MapGet("/credit-contracts/{id:guid}/renewals",RenewalHistory);
        g.MapPost("/credit-contracts/{id:guid}/renew",Renew);
        g.MapPost("/credit-contracts/{id:guid}/activate",Activate);
        g.MapPost("/credit-contracts/{id:guid}/cancel",Cancel);
        g.MapPost("/credit-contracts/{id:guid}/signed-document",AttachDocument);
        return g;
    }
    private static bool Read(HttpContext h)=>FinanceAccess.Has(h,"crm.contract.read");
    private static bool Manage(HttpContext h)=>FinanceAccess.Has(h,"crm.contract.manage");
    private static bool Valid(DateTime start,DateTime renew,DateTime? expiry)=>
        FinanceAccess.Utc(start)&&FinanceAccess.Utc(renew)&&
        (!expiry.HasValue||FinanceAccess.Utc(expiry.Value))&&renew>=start&&
        (!expiry.HasValue||expiry.Value>=start);
    private static Task<bool> Can(CreditContract x,CrmDbContext db,HttpContext h,CancellationToken ct)=>
        FinanceAccess.Allowed(x.AccountId,db,h,ct);
    private static async Task<IResult> List(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h)||!await FinanceAccess.Allowed(accountId,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.CreditContracts.AsNoTracking().Where(x=>x.AccountId==accountId)
            .OrderByDescending(x=>x.CreatedAt).Take(100).ToArrayAsync(ct));
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        var c=await db.CreditContracts.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        return c==null?Results.NotFound():await Can(c,db,h,ct)?Results.Ok(c):CrmAccess.Denied();
    }
    private static async Task<IResult> RenewalHistory(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        var c=await db.CreditContracts.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(c==null)return Results.NotFound();
        if(!await Can(c,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.CreditContractRenewals.AsNoTracking().Where(x=>x.ContractId==id)
            .OrderByDescending(x=>x.CreatedAt).Take(200).ToArrayAsync(ct));
    }
    private static async Task<IResult> Create(Guid accountId,CreateCreditContractRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h)||!await FinanceAccess.Allowed(accountId,db,h,ct))return CrmAccess.Denied();
        if(!Valid(r.StartDate,r.RenewalDate,r.ExpiryDate)||r.PaymentTerms is null||
            r.PaymentTerms.Length>4000 || r.ContractNumber?.Length>100)
            return FinanceAccess.Bad("Invalid dates, terms or contract identifier");
        if(r.CreditProfileId.HasValue&&
            !await db.CreditProfiles.AnyAsync(x=>x.Id==r.CreditProfileId&&x.AccountId==accountId&&x.Status=="Approved",ct))
            return FinanceAccess.Conflict("Only an approved credit profile from this account can be contracted");
        var actor=FinanceAccess.Actor(h);
        var contract=new CreditContract {
            AccountId=accountId,CreditProfileId=r.CreditProfileId,
            ContractNumber=string.IsNullOrWhiteSpace(r.ContractNumber)?null:r.ContractNumber.Trim(),
            StartDate=r.StartDate,RenewalDate=r.RenewalDate,ExpiryDate=r.ExpiryDate,
            PaymentTerms=r.PaymentTerms.Trim(),AutoRenew=r.AutoRenew,
            CreatedByUserId=actor};
        db.CreditContracts.Add(contract);
        var saved=await FinanceAccess.Save(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/credit-contracts/"+contract.Id,contract):saved;
    }
    private static async Task<IResult> Activate(Guid id,UpdateContractStatusRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        var c=await db.CreditContracts.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(c==null)return Results.NotFound();
        if(!await Can(c,db,h,ct))return CrmAccess.Denied();
        if(c.RowVersion!=r.ExpectedVersion||c.Status!="Draft")
            return FinanceAccess.Conflict("Only current draft contract can be activated");
        if(c.CreditProfileId.HasValue&&!await db.CreditProfiles.AnyAsync(x=>x.Id==c.CreditProfileId&&
            x.AccountId==c.AccountId&&x.Status=="Approved",ct))
            return FinanceAccess.Conflict("Contract credit profile is no longer approved");
        if(c.ExpiryDate.HasValue&&c.ExpiryDate<=DateTime.UtcNow)
            return FinanceAccess.Conflict("Expired contract cannot activate");
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>1000)
            return FinanceAccess.Bad("Activation reason required");
        c.Status="Active";c.RowVersion++;c.UpdatedAt=DateTime.UtcNow;c.UpdatedByUserId=FinanceAccess.Actor(h);
        db.CreditContractRenewals.Add(new CreditContractRenewal {
            ContractId=id,ChangedBy=FinanceAccess.Actor(h),CreatedByUserId=FinanceAccess.Actor(h),
            PreviousRenewalDate=c.RenewalDate,NewRenewalDate=c.RenewalDate,
            PreviousExpiryDate=c.ExpiryDate,NewExpiryDate=c.ExpiryDate,
            Reason="Activation: "+r.Reason.Trim()});
        var saved=await FinanceAccess.Save(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(c):saved;
    }
    private static async Task<IResult> Cancel(Guid id,UpdateContractStatusRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>1000)return FinanceAccess.Bad("Reason required");
        var c=await db.CreditContracts.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(c==null)return Results.NotFound();
        if(!await Can(c,db,h,ct))return CrmAccess.Denied();
        if(c.RowVersion!=r.ExpectedVersion||c.Status is "Cancelled" or "Expired")
            return FinanceAccess.Conflict("Contract state or version changed");
        c.Status="Cancelled";c.RowVersion++;c.UpdatedAt=DateTime.UtcNow;c.UpdatedByUserId=FinanceAccess.Actor(h);
        db.CreditContractRenewals.Add(new CreditContractRenewal {
            ContractId=id,ChangedBy=FinanceAccess.Actor(h),CreatedByUserId=FinanceAccess.Actor(h),
            PreviousRenewalDate=c.RenewalDate,NewRenewalDate=c.RenewalDate,
            PreviousExpiryDate=c.ExpiryDate,NewExpiryDate=c.ExpiryDate,
            Reason="Cancellation: "+r.Reason.Trim()});
        var saved=await FinanceAccess.Save(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(c):saved;
    }
    private static async Task<IResult> Renew(Guid id,RenewCreditContractRequest r,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(r.OperationId==Guid.Empty||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>1000||
            !FinanceAccess.Utc(r.RenewalDate)||(r.ExpiryDate.HasValue&&!FinanceAccess.Utc(r.ExpiryDate.Value)))
            return FinanceAccess.Bad("Valid operation, new date and reason required");
        var duplicate=await db.CreditContractRenewals.AsNoTracking()
            .FirstOrDefaultAsync(x=>x.OperationId==r.OperationId,ct);
        if(duplicate!=null)
        {
            var contract=await db.CreditContracts.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
            return contract!=null&&await Can(contract,db,h,ct)&&duplicate.ContractId==id?
                Results.Ok(duplicate):CrmAccess.Denied();
        }
        var c=await db.CreditContracts.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(c==null)return Results.NotFound();
        if(!await Can(c,db,h,ct))return CrmAccess.Denied();
        if(c.Status!="Active"||c.RowVersion!=r.ExpectedVersion)
            return FinanceAccess.Conflict("Renewal needs active current contract");
        if(!Valid(c.StartDate,r.RenewalDate,r.ExpiryDate) || r.RenewalDate<=c.RenewalDate)
            return FinanceAccess.Bad("Renewal date must advance and dates remain valid");
        var actor=FinanceAccess.Actor(h);
        var history=new CreditContractRenewal {ContractId=id,OperationId=r.OperationId,
            ChangedBy=actor,CreatedByUserId=actor,PreviousRenewalDate=c.RenewalDate,
            NewRenewalDate=r.RenewalDate,PreviousExpiryDate=c.ExpiryDate,NewExpiryDate=r.ExpiryDate,
            Reason=r.Reason.Trim()};
        c.RenewalDate=r.RenewalDate;c.ExpiryDate=r.ExpiryDate;c.RowVersion++;
        c.UpdatedAt=DateTime.UtcNow;c.UpdatedByUserId=actor;
        db.CreditContractRenewals.Add(history);
        var saved=await FinanceAccess.Save(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(c):saved;
    }
    private static async Task<IResult> AttachDocument(Guid id,AttachSignedContractRequest r,
        CrmDbContext db,HttpContext h,IHttpClientFactory clients,IConfiguration config,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(r.FileId==Guid.Empty)return FinanceAccess.Bad("Storage file ID required");
        var c=await db.CreditContracts.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(c==null)return Results.NotFound();
        if(!await Can(c,db,h,ct))return CrmAccess.Denied();
        if(c.RowVersion!=r.ExpectedVersion||c.Status=="Cancelled")
            return FinanceAccess.Conflict("Contract changed or cancelled");
        // Storage is the source of truth; a random GUID is never sufficient proof of upload.
        var url=config["Integrations:Storage:BaseUrl"];
        if(string.IsNullOrWhiteSpace(url)||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||
            uri.Scheme is not ("http" or "https")||uri.UserInfo.Length>0)
            return Results.Problem(statusCode:503,title:"Storage integration is not configured");
        var auth=h.Request.Headers.Authorization.ToString();
        if(!auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();
        using var request=new HttpRequestMessage(HttpMethod.Get,url.TrimEnd('/')+
            "/api/v1/storage/files/"+r.FileId);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",auth[7..].Trim());
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response=await clients.CreateClient().SendAsync(request,timeout.Token);
            if(response.StatusCode==HttpStatusCode.NotFound)return FinanceAccess.Bad("Document does not exist in Storage");
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return CrmAccess.Denied();
            if(!response.IsSuccessStatusCode)return Results.Problem(statusCode:503,title:"Storage not available");
            using var body=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),cancellationToken:timeout.Token);
            var file=body.RootElement;
            if(!file.TryGetProperty("id",out var idValue)||idValue.GetGuid()!=r.FileId||
               !file.TryGetProperty("references",out var refs)||refs.ValueKind!=JsonValueKind.Array)
                return Results.Problem(statusCode:502,title:"Invalid Storage response");
            var linked=refs.EnumerateArray().Any(x=>
                x.TryGetProperty("sourceService",out var source)&&
                source.GetString()=="DholeCRMService"&&
                x.TryGetProperty("entityType",out var entity)&&entity.GetString()=="CreditContract"&&
                x.TryGetProperty("entityId",out var target)&&target.GetGuid()==id);
            if(!linked)return FinanceAccess.Conflict("Storage document must be attached to this CRM contract");
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {return Results.Problem(statusCode:504,title:"Storage request timed out");}
        catch(HttpRequestException){return Results.Problem(statusCode:503,title:"Storage unavailable");}
        catch(JsonException){return Results.Problem(statusCode:502,title:"Invalid Storage JSON");}
        c.SignedDocumentFileId=r.FileId;c.RowVersion++;c.UpdatedAt=DateTime.UtcNow;
        c.UpdatedByUserId=FinanceAccess.Actor(h);
        var saved=await FinanceAccess.Save(db,ct);
        return saved is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(c):saved;
    }
}
