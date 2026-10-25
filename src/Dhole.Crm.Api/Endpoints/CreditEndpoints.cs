using System.Data;
using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Api.Endpoints;

public sealed record CreditTermsRequest(bool CreditEnabled,int CreditDays,decimal CreditLimit,
    string CurrencyCode,decimal MonthlyFinancingPct,string PaymentConditions,
    DateTime EffectiveFrom,DateTime? ReviewAt);
public sealed record CreditApprovalRequest(long ExpectedVersion,string Reason);
public sealed record CreditCoverageRequest(long ExpectedVersion,string ServiceCode,string CoverageType,string Description);
public sealed record CreditChangeRequest(long ExpectedVersion,CreditTermsRequest Terms,string Reason);
internal static class CreditEndpoints
{
    public static RouteGroupBuilder MapCredit(this RouteGroupBuilder g)
    {
        g.MapGet("/accounts/{accountId:guid}/credit-profiles",List);
        g.MapPost("/accounts/{accountId:guid}/credit-profiles",Propose);
        g.MapGet("/credit-profiles/{id:guid}",Get);
        g.MapGet("/credit-profiles/{id:guid}/history",History);
        g.MapGet("/credit-profiles/{id:guid}/coverages",Coverages);
        g.MapPost("/credit-profiles/{id:guid}/revise",Revise);
        g.MapPost("/credit-profiles/{id:guid}/approve",Approve);
        g.MapPost("/credit-profiles/{id:guid}/reject",Reject);
        g.MapPost("/credit-profiles/{id:guid}/coverages",AddCoverage);
        g.MapPost("/credit-profiles/{id:guid}/coverages/{coverageId:guid}/deactivate",RemoveCoverage);
        return g;
    }
    private static bool Read(HttpContext h)=>FinanceAccess.Has(h,"crm.credit.read");
    private static bool Manage(HttpContext h)=>FinanceAccess.Has(h,"crm.credit.manage");
    private static bool Approver(HttpContext h)=>FinanceAccess.Has(h,"crm.credit.approve");
    private static bool Valid(CreditTermsRequest r)=>
        r.CreditDays is >=0 and <=3650 && r.CreditLimit>=0 &&
        decimal.Round(r.CreditLimit,2)==r.CreditLimit &&
        r.MonthlyFinancingPct is >=0 and <=100 &&
        decimal.Round(r.MonthlyFinancingPct,4)==r.MonthlyFinancingPct &&
        Regex.IsMatch(r.CurrencyCode??"",@"^[A-Za-z]{3}$") &&
        r.PaymentConditions is not null && r.PaymentConditions.Length<=4000 &&
        FinanceAccess.Utc(r.EffectiveFrom) &&
        (!r.ReviewAt.HasValue||(FinanceAccess.Utc(r.ReviewAt.Value)&&r.ReviewAt>r.EffectiveFrom));
    private static void Apply(CreditProfile p,CreditTermsRequest r)
    {
        p.CreditEnabled=r.CreditEnabled;p.CreditDays=r.CreditDays;p.CreditLimit=r.CreditLimit;
        p.CurrencyCode=r.CurrencyCode.ToUpperInvariant();p.MonthlyFinancingPct=r.MonthlyFinancingPct;
        p.PaymentConditions=r.PaymentConditions.Trim();p.EffectiveFrom=r.EffectiveFrom;p.ReviewAt=r.ReviewAt;
    }
    private static async Task<bool> Can(CreditProfile p,CrmDbContext db,HttpContext h,CancellationToken ct)
        =>await FinanceAccess.Allowed(p.AccountId,db,h,ct);
    private static async Task<IResult> List(Guid accountId,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h)||!await FinanceAccess.Allowed(accountId,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.CreditProfiles.AsNoTracking().Where(x=>x.AccountId==accountId)
            .OrderByDescending(x=>x.CreatedAt).Take(100).ToArrayAsync(ct));
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        var p=await db.CreditProfiles.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        return p==null?Results.NotFound():await Can(p,db,h,ct)?Results.Ok(p):CrmAccess.Denied();
    }
    private static async Task<IResult> History(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        var p=await db.CreditProfiles.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.CreditTermsHistory.AsNoTracking().Where(x=>x.CreditProfileId==id)
            .OrderBy(x=>x.ChangedAt).Take(200).ToArrayAsync(ct));
    }
    private static async Task<IResult> Coverages(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Read(h))return CrmAccess.Denied();
        var p=await db.CreditProfiles.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.CreditCoverages.AsNoTracking().Where(x=>x.CreditProfileId==id)
            .OrderBy(x=>x.ServiceCode).ToArrayAsync(ct));
    }
    private static async Task<bool> Currency(IConfigReferenceService config,string code,CancellationToken ct)=>
        await config.IsActiveCodeAsync("currencies",code.ToUpperInvariant(),ct);
    private static async Task<IResult> Propose(Guid accountId,CreditTermsRequest req,CrmDbContext db,
        HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!Manage(h)||!await FinanceAccess.Allowed(accountId,db,h,ct))return CrmAccess.Denied();
        if(!Valid(req))return FinanceAccess.Bad("Invalid financing percentage, currency or credit terms");
        try{if(!await Currency(config,req.CurrencyCode,ct))return FinanceAccess.Bad("Inactive Config currency");}
        catch(Exception) when(!ct.IsCancellationRequested){return CrmDataEndpoints.Unavailable();}
        if(await db.CreditProfiles.AnyAsync(x=>x.AccountId==accountId&&x.Status=="PendingApproval",ct))
            return FinanceAccess.Conflict("Pending credit proposal already exists");
        var actor=FinanceAccess.Actor(h);var p=new CreditProfile{AccountId=accountId,CreatedByUserId=actor};
        Apply(p,req);db.CreditProfiles.Add(p);
        FinanceAccess.Audit(db,p,actor,"{}","Created pending credit proposal");
        var result=await FinanceAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/credit-profiles/"+p.Id,p):result;
    }
    private static async Task<IResult> Revise(Guid id,CreditChangeRequest req,CrmDbContext db,
        HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(!Valid(req.Terms)||string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>1000)
            return FinanceAccess.Bad("Valid revised terms and reason required");
        var old=await db.CreditProfiles.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(old==null)return Results.NotFound();
        if(!await Can(old,db,h,ct))return CrmAccess.Denied();
        if(old.Status!="Approved"||old.RowVersion!=req.ExpectedVersion)
            return FinanceAccess.Conflict("Only a current approved profile may be revised");
        try{if(!await Currency(config,req.Terms.CurrencyCode,ct))return FinanceAccess.Bad("Inactive Config currency");}
        catch(Exception) when(!ct.IsCancellationRequested){return CrmDataEndpoints.Unavailable();}
        if(await db.CreditProfiles.AnyAsync(x=>x.AccountId==old.AccountId&&x.Status=="PendingApproval",ct))
            return FinanceAccess.Conflict("Pending credit proposal already exists");
        var actor=FinanceAccess.Actor(h);var p=new CreditProfile{AccountId=old.AccountId,CreatedByUserId=actor};
        Apply(p,req.Terms);db.CreditProfiles.Add(p);
        var covers=await db.CreditCoverages.AsNoTracking().Where(x=>x.CreditProfileId==id&&x.IsActive).ToListAsync(ct);
        foreach(var c in covers)db.CreditCoverages.Add(new CreditCoverage {
            CreditProfileId=p.Id,ServiceCode=c.ServiceCode,CoverageType=c.CoverageType,
            Description=c.Description,CreatedByUserId=actor});
        FinanceAccess.Audit(db,p,actor,FinanceAccess.Snapshot(old),req.Reason.Trim());
        var result=await FinanceAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/credit-profiles/"+p.Id,p):result;
    }
    private static async Task<IResult> Approve(Guid id,CreditApprovalRequest req,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Approver(h))return CrmAccess.Denied();
        if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>1000)
            return FinanceAccess.Bad("Approval reason required");
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var p=await db.CreditProfiles.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        if(p.Status!="PendingApproval"||p.RowVersion!=req.ExpectedVersion)
            return FinanceAccess.Conflict("Credit approval conflicts with current version");
        var actor=FinanceAccess.Actor(h);var now=DateTime.UtcNow;
        foreach(var x in await db.CreditProfiles.Where(x=>x.AccountId==p.AccountId&&x.Status=="Approved").ToListAsync(ct))
        {
            var before=FinanceAccess.Snapshot(x);
            x.Status="Superseded";x.UpdatedAt=now;x.UpdatedByUserId=actor;x.RowVersion++;
            FinanceAccess.Audit(db,x,actor,before,"Superseded by approved revision");
        }
        var prior=FinanceAccess.Snapshot(p);
        p.Status="Approved";p.ApprovedByUserId=actor;p.ApprovedAt=now;
        p.UpdatedAt=now;p.UpdatedByUserId=actor;p.RowVersion++;
        FinanceAccess.Audit(db,p,actor,prior,req.Reason.Trim());
        var result=await FinanceAccess.Save(db,ct);
        if(result is not Microsoft.AspNetCore.Http.HttpResults.Ok)return result;
        await tx.CommitAsync(ct);return Results.Ok(p);
    }
    private static async Task<IResult> Reject(Guid id,CreditApprovalRequest req,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Approver(h))return CrmAccess.Denied();
        if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>1000)
            return FinanceAccess.Bad("Rejection reason required");
        var p=await db.CreditProfiles.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        if(p.Status!="PendingApproval"||p.RowVersion!=req.ExpectedVersion)
            return FinanceAccess.Conflict("Credit proposal already resolved");
        var before=FinanceAccess.Snapshot(p);
        p.Status="Rejected";p.RowVersion++;p.UpdatedAt=DateTime.UtcNow;p.UpdatedByUserId=FinanceAccess.Actor(h);
        FinanceAccess.Audit(db,p,FinanceAccess.Actor(h),before,req.Reason.Trim());
        var result=await FinanceAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(p):result;
    }
    private static async Task<IResult> AddCoverage(Guid id,CreditCoverageRequest req,CrmDbContext db,
        HttpContext h,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(req.CoverageType is not ("INCLUDED" or "EXCLUDED")||
            !Regex.IsMatch(req.ServiceCode??"",@"^[A-Za-z0-9_-]{1,80}$")||
            req.Description is null||req.Description.Length>2000)
            return FinanceAccess.Bad("Invalid service coverage");
        var p=await db.CreditProfiles.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        if(p.Status!="PendingApproval"||p.RowVersion!=req.ExpectedVersion)
            return FinanceAccess.Conflict("Only pending proposals can modify coverages");
        var code=req.ServiceCode.ToUpperInvariant();
        if(await db.CreditCoverages.AnyAsync(x=>x.CreditProfileId==id&&x.ServiceCode==code&&x.IsActive,ct))
            return FinanceAccess.Conflict("An active included/excluded condition already exists for service");
        var actor=FinanceAccess.Actor(h);var before=FinanceAccess.Snapshot(p);
        var coverage=new CreditCoverage{CreditProfileId=id,ServiceCode=code,
            CoverageType=req.CoverageType,Description=req.Description.Trim(),CreatedByUserId=actor};
        db.CreditCoverages.Add(coverage);
        p.RowVersion++;p.UpdatedAt=DateTime.UtcNow;p.UpdatedByUserId=actor;
        FinanceAccess.Audit(db,p,actor,before,"Added coverage "+code+" "+req.CoverageType);
        var result=await FinanceAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?
            Results.Created("/api/crm/credit-profiles/"+id+"/coverages",coverage):result;
    }
    private static async Task<IResult> RemoveCoverage(Guid id,Guid coverageId,CreditApprovalRequest req,
        CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!Manage(h))return CrmAccess.Denied();
        if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>1000)
            return FinanceAccess.Bad("Reason required");
        var p=await db.CreditProfiles.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(p==null)return Results.NotFound();
        if(!await Can(p,db,h,ct))return CrmAccess.Denied();
        if(p.Status!="PendingApproval"||p.RowVersion!=req.ExpectedVersion)
            return FinanceAccess.Conflict("Only pending coverages can be retired");
        var c=await db.CreditCoverages.FirstOrDefaultAsync(x=>x.Id==coverageId&&x.CreditProfileId==id&&x.IsActive,ct);
        if(c==null)return Results.NotFound();
        var actor=FinanceAccess.Actor(h);var before=FinanceAccess.Snapshot(p);
        c.IsActive=false;c.RowVersion++;c.UpdatedAt=DateTime.UtcNow;c.UpdatedByUserId=actor;
        p.RowVersion++;p.UpdatedAt=DateTime.UtcNow;p.UpdatedByUserId=actor;
        FinanceAccess.Audit(db,p,actor,before,req.Reason.Trim()+" retired coverage "+c.ServiceCode);
        var result=await FinanceAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok?Results.Ok(c):result;
    }
}
