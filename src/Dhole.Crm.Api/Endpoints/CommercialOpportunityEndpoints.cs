using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record OpportunityCreateRequest(Guid? AccountId,Guid? LeadId,string Title,Guid? ExecutiveUserId,
    Guid? PipelineId,Guid? StageId,decimal AmountEstimated,string CurrencyCode,
    DateTime? ExpectedCloseAt,string? Description);
public sealed record OpportunityPatchRequest(long ExpectedVersion,string Title,decimal AmountEstimated,
    string CurrencyCode,DateTime? ExpectedCloseAt,string? Description);
public sealed record OpportunityMoveRequest(long ExpectedVersion,Guid StageId,string? Reason);
public sealed record OpportunityCloseRequest(long ExpectedVersion,Guid StageId,string? LossReasonCode,string? Reason);
public sealed record OpportunityAssignRequest(long ExpectedVersion,Guid ExecutiveUserId,string Reason);
public sealed record OpportunityContactRequest(Guid ContactId,string DecisionRole);
public sealed record NegotiationCreateRequest(Guid? PricingQuoteId,string Status,string ChangeSummary,
    string? AgreedTerms,DateTime? NextActionAt);

internal static class CommercialOpportunityEndpoints
{
    public static RouteGroupBuilder MapCommercialOpportunities(this RouteGroupBuilder g)
    {
        g.MapGet("/opportunities",List);
        g.MapGet("/opportunities/{id:guid}",Get);
        g.MapPost("/opportunities",Create);
        g.MapPatch("/opportunities/{id:guid}",Patch);
        g.MapPost("/opportunities/{id:guid}/move-stage",MoveStage);
        g.MapPost("/opportunities/{id:guid}/close",Close);
        g.MapPost("/opportunities/{id:guid}/reopen",Reopen);
        g.MapPost("/opportunities/{id:guid}/assign",Assign);
        g.MapGet("/opportunities/{id:guid}/stage-history",StageHistory);
        g.MapGet("/opportunities/{id:guid}/contacts",Contacts);
        g.MapPost("/opportunities/{id:guid}/contacts",AddContact);
        g.MapGet("/opportunities/{id:guid}/negotiations",Negotiations);
        g.MapPost("/opportunities/{id:guid}/negotiations",AddNegotiation);
        return g;
    }
    private static async Task<IResult> List(CrmDbContext db,HttpContext h,
        string? q,Guid? pipelineId,string? status,int? page,int? pageSize,CancellationToken ct)
    {
        if(!CommercialAccess.OpportunityCanRead(h))return CrmAccess.Denied();
        var query=db.Opportunities.AsNoTracking().Where(x=>x.ArchivedAt==null);
        if(!CrmAccess.Scope(h,"crm.opportunity.read-all"))
            query=query.Where(x=>x.ExecutiveUserId==CrmAccess.UserId(h));
        if(pipelineId is { } p)query=query.Where(x=>x.PipelineId==p);
        if(!string.IsNullOrWhiteSpace(status))query=query.Where(x=>x.Status==status);
        if(!string.IsNullOrWhiteSpace(q))query=query.Where(x=>x.Title.ToLower().Contains(q.Trim().ToLower()));
        var total=await query.CountAsync(ct);
        var n=Math.Max(1,page??1);var size=Math.Clamp(pageSize??25,1,100);
        return Results.Ok(new {total,page=n,pageSize=size,items=await query
            .OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip((n-1)*size).Take(size).ToListAsync(ct)});
    }
    private static async Task<IResult> Get(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CommercialAccess.OpportunityCanRead(h))return CrmAccess.Denied();
        var o=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id && x.ArchivedAt==null,ct);
        if(o is null)return Results.NotFound();
        return CommercialAccess.OpportunityOwns(h,o)?Results.Ok(o):CrmAccess.Denied();
    }
    private static async Task<IResult> Create(OpportunityCreateRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.create"))return CrmAccess.Denied();
        if(!CommercialRules.ValidOpportunitySubject(r.AccountId,r.LeadId) ||
            string.IsNullOrWhiteSpace(r.Title)||r.Title.Length>250 ||
            !CommercialRules.ValidMoney(r.AmountEstimated,0) ||
            r.CurrencyCode.Length is < 1 or > 12)
            return CommercialAccess.Invalid("Invalid opportunity subject, title, amount or currency");
        var caller=CrmAccess.UserId(h);
        if(!await CommercialAccess.SubjectCanRead(db,h,r.AccountId,r.LeadId,null,ct))
            return CrmAccess.Denied();
        var executive=r.ExecutiveUserId??caller;
        if(executive!=caller && !CrmAccess.Scope(h,"crm.opportunity.assign"))
            return CrmAccess.Denied();
        if(await auth.GetActiveAsync(executive,ct) is null)
            return CommercialAccess.Invalid("Assigned user is inactive");
        if(!await config.IsActiveCodeAsync("currencies",r.CurrencyCode,ct))
            return CommercialAccess.Invalid("Currency is not active in Config");
        var pipeline=r.PipelineId.HasValue
            ? await db.SalesPipelines.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==r.PipelineId&&x.IsActive,ct)
            : await db.SalesPipelines.AsNoTracking().FirstOrDefaultAsync(x=>x.IsDefault&&x.IsActive,ct);
        if(pipeline is null)return CommercialAccess.Invalid("There is no active CRM pipeline");
        var stage=r.StageId.HasValue
            ? await db.SalesPipelineStages.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==r.StageId&&x.PipelineId==pipeline.Id&&x.IsActive,ct)
            : await db.SalesPipelineStages.AsNoTracking().Where(x=>x.PipelineId==pipeline.Id&&x.IsActive&&!x.IsWon&&!x.IsLost)
                .OrderBy(x=>x.SortOrder).FirstOrDefaultAsync(ct);
        if(stage is null||stage.IsWon||stage.IsLost)return CommercialAccess.Invalid("Choose an open stage in this pipeline");
        var opportunity=new Opportunity {
            AccountId=r.AccountId,LeadId=r.LeadId,Title=r.Title.Trim(),ExecutiveUserId=executive,
            PipelineId=pipeline.Id,StageId=stage.Id,AmountEstimated=r.AmountEstimated,
            CurrencyCode=r.CurrencyCode.Trim().ToUpperInvariant(),ProbabilityPct=stage.DefaultWinProbability,
            ExpectedCloseAt=r.ExpectedCloseAt,Description=r.Description,CreatedByUserId=caller
        };
        db.Opportunities.Add(opportunity);
        db.OpportunityStageHistories.Add(new OpportunityStageHistory {
            OpportunityId=opportunity.Id,ToStageId=stage.Id,ChangedBy=caller,
            Reason="Creación",CreatedByUserId=caller
        });
        try{await db.SaveChangesAsync(ct);return Results.Created("/api/crm/opportunities/"+opportunity.Id,opportunity);}
        catch(DbUpdateException){return CommercialAccess.Conflict("Opportunity or stage constraint");}
    }
    private static async Task<IResult> Patch(Guid id,OpportunityPatchRequest r,CrmDbContext db,
        HttpContext h,IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.update"))return CrmAccess.Denied();
        var o=await db.Opportunities.FindAsync([id],ct);
        if(o is null||o.ArchivedAt!=null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(o.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale opportunity version");
        if(o.Status!="Open")return CommercialAccess.Conflict("Closed opportunity must be reopened before editing");
        if(string.IsNullOrWhiteSpace(r.Title)||r.Title.Length>250||
            !CommercialRules.ValidMoney(r.AmountEstimated,0)||r.CurrencyCode.Length is < 1 or > 12)
            return CommercialAccess.Invalid("Invalid opportunity values");
        if(!await config.IsActiveCodeAsync("currencies",r.CurrencyCode,ct))
            return CommercialAccess.Invalid("Currency unknown in Config");
        o.Title=r.Title.Trim();o.AmountEstimated=r.AmountEstimated;
        o.CurrencyCode=r.CurrencyCode.Trim().ToUpperInvariant();o.ExpectedCloseAt=r.ExpectedCloseAt;
        o.Description=r.Description;o.RowVersion++;o.UpdatedAt=DateTime.UtcNow;o.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(o):save;
    }
    private static async Task<IResult> MoveStage(Guid id,OpportunityMoveRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.update"))return CrmAccess.Denied();
        var o=await db.Opportunities.FindAsync([id],ct);
        if(o is null||o.ArchivedAt!=null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(o.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale opportunity version");
        if(o.Status!="Open")return CommercialAccess.Conflict("Closed opportunities cannot move stages");
        var stage=await db.SalesPipelineStages.AsNoTracking().FirstOrDefaultAsync(
            x=>x.Id==r.StageId&&x.PipelineId==o.PipelineId&&x.IsActive,ct);
        if(stage is null||stage.IsWon||stage.IsLost)
            return CommercialAccess.Invalid("Terminal stages require explicit close and loss reason");
        if(stage.Id==o.StageId)return Results.Ok(o);
        var prior=o.StageId;
        o.StageId=stage.Id;o.ProbabilityPct=stage.DefaultWinProbability;
        o.RowVersion++;o.UpdatedAt=DateTime.UtcNow;o.UpdatedByUserId=CrmAccess.UserId(h);
        db.OpportunityStageHistories.Add(new OpportunityStageHistory {
            OpportunityId=o.Id,FromStageId=prior,ToStageId=stage.Id,
            ChangedBy=CrmAccess.UserId(h),Reason=r.Reason,CreatedByUserId=CrmAccess.UserId(h)
        });
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(o):save;
    }
    private static async Task<IResult> Close(Guid id,OpportunityCloseRequest r,CrmDbContext db,HttpContext h,
        IConfigReferenceService config,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.close"))return CrmAccess.Denied();
        var o=await db.Opportunities.FindAsync([id],ct);
        if(o is null||o.ArchivedAt!=null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(o.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale opportunity version");
        if(o.Status!="Open")return CommercialAccess.Conflict("Opportunity already closed");
        var stage=await db.SalesPipelineStages.AsNoTracking().FirstOrDefaultAsync(
            x=>x.Id==r.StageId&&x.PipelineId==o.PipelineId&&x.IsActive,ct);
        if(stage is null||!CommercialRules.ValidClose(stage.IsWon,stage.IsLost,r.LossReasonCode))
            return CommercialAccess.Invalid("A terminal stage and valid loss reason are required");
        if(stage.IsLost&&!await config.IsActiveCodeAsync("crm-opportunity-loss-reasons",r.LossReasonCode!,ct))
            return CommercialAccess.Invalid("Unknown loss reason");
        var old=o.StageId;
        o.StageId=stage.Id;o.Status=stage.IsWon?"Won":"Lost";
        o.ProbabilityPct=stage.DefaultWinProbability;o.LossReasonCode=stage.IsLost?r.LossReasonCode:null;
        o.ActualCloseAt=DateTime.UtcNow;o.UpdatedAt=DateTime.UtcNow;o.RowVersion++;
        o.UpdatedByUserId=CrmAccess.UserId(h);
        db.OpportunityStageHistories.Add(new OpportunityStageHistory {
            OpportunityId=o.Id,FromStageId=old,ToStageId=stage.Id,ChangedBy=CrmAccess.UserId(h),
            Reason=r.Reason,CreatedByUserId=CrmAccess.UserId(h)
        });
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(o):save;
    }
    private static async Task<IResult> Reopen(Guid id,OpportunityMoveRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.close"))return CrmAccess.Denied();
        var o=await db.Opportunities.FindAsync([id],ct);
        if(o is null||o.ArchivedAt!=null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(o.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale opportunity version");
        if(o.Status=="Open")return CommercialAccess.Conflict("Opportunity is already open");
        if(string.IsNullOrWhiteSpace(r.Reason))return CommercialAccess.Invalid("Reopening reason is required");
        var stage=await db.SalesPipelineStages.AsNoTracking().FirstOrDefaultAsync(
            x=>x.Id==r.StageId&&x.PipelineId==o.PipelineId&&x.IsActive&&!x.IsLost&&!x.IsWon,ct);
        if(stage is null)return CommercialAccess.Invalid("A nonterminal stage from the same pipeline is required");
        var old=o.StageId;
        o.StageId=stage.Id;o.Status="Open";o.ActualCloseAt=null;o.LossReasonCode=null;
        o.ProbabilityPct=stage.DefaultWinProbability;o.RowVersion++;o.UpdatedAt=DateTime.UtcNow;
        o.UpdatedByUserId=CrmAccess.UserId(h);
        db.OpportunityStageHistories.Add(new OpportunityStageHistory {
            OpportunityId=o.Id,FromStageId=old,ToStageId=stage.Id,ChangedBy=CrmAccess.UserId(h),
            Reason=r.Reason,CreatedByUserId=CrmAccess.UserId(h)
        });
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(o):save;
    }
    private static async Task<IResult> Assign(Guid id,OpportunityAssignRequest r,CrmDbContext db,HttpContext h,
        IAuthReferenceService auth,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.assign"))return CrmAccess.Denied();
        var o=await db.Opportunities.FindAsync([id],ct);
        if(o is null||o.ArchivedAt!=null)return Results.NotFound();
        if(o.RowVersion!=r.ExpectedVersion)return CommercialAccess.Conflict("Stale opportunity version");
        if(string.IsNullOrWhiteSpace(r.Reason))return CommercialAccess.Invalid("Assignment reason required");
        if(await auth.GetActiveAsync(r.ExecutiveUserId,ct) is null)return CommercialAccess.Invalid("Executive inactive");
        o.ExecutiveUserId=r.ExecutiveUserId;o.RowVersion++;o.UpdatedAt=DateTime.UtcNow;
        o.UpdatedByUserId=CrmAccess.UserId(h);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(o):save;
    }
    private static async Task<bool> Allowed(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        var o=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        return o is not null&&CommercialAccess.OpportunityOwns(h,o);
    }
    private static async Task<IResult> StageHistory(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CommercialAccess.OpportunityCanRead(h)||!await Allowed(id,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.OpportunityStageHistories.AsNoTracking().Where(x=>x.OpportunityId==id)
            .OrderBy(x=>x.ChangedAt).ToListAsync(ct));
    }
    private static async Task<IResult> Contacts(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CommercialAccess.OpportunityCanRead(h)||!CrmAccess.Scope(h,"crm.contact.read")||
            !await Allowed(id,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.OpportunityContacts.AsNoTracking().Where(x=>x.OpportunityId==id).ToListAsync(ct));
    }
    private static async Task<IResult> AddContact(Guid id,OpportunityContactRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.update")||!CrmAccess.Scope(h,"crm.contact.read"))
            return CrmAccess.Denied();
        var o=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        if(o is null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(!await CommercialAccess.ContactBelongsTo(db,r.ContactId,o.AccountId,o.Id,ct))
            return CommercialAccess.Invalid("Contact must belong to opportunity account");
        if(string.IsNullOrWhiteSpace(r.DecisionRole)||r.DecisionRole.Length>60)
            return CommercialAccess.Invalid("Invalid decision role");
        var link=await db.OpportunityContacts.FindAsync([id,r.ContactId],ct);
        if(link is not null){return Results.Ok(link);}
        link=new OpportunityContact {OpportunityId=id,ContactId=r.ContactId,DecisionRole=r.DecisionRole.Trim()};
        db.OpportunityContacts.Add(link);
        var save=await CommercialAccess.Save(db,ct);
        return save is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Created("/api/crm/opportunities/"+id+"/contacts",link):save;
    }
    private static async Task<IResult> Negotiations(Guid id,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CommercialAccess.OpportunityCanRead(h)||!await Allowed(id,db,h,ct))return CrmAccess.Denied();
        return Results.Ok(await db.OpportunityNegotiations.AsNoTracking().Where(x=>x.OpportunityId==id)
            .OrderByDescending(x=>x.CreatedAt).ToListAsync(ct));
    }
    private static async Task<IResult> AddNegotiation(Guid id,NegotiationCreateRequest r,CrmDbContext db,HttpContext h,CancellationToken ct)
    {
        if(!CrmAccess.Scope(h,"crm.opportunity.update"))return CrmAccess.Denied();
        var o=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,ct);
        if(o is null)return Results.NotFound();
        if(!CommercialAccess.OpportunityOwns(h,o))return CrmAccess.Denied();
        if(o.Status!="Open")return CommercialAccess.Conflict("Closed opportunity");
        if(r.PricingQuoteId is not null)
            return CommercialAccess.Invalid("Pricing quote link will be verified in phase 4");
        if(string.IsNullOrWhiteSpace(r.ChangeSummary)||r.ChangeSummary.Length>8000||
            r.Status is not ("Open" or "Agreed" or "Rejected"))
            return CommercialAccess.Invalid("Invalid negotiation");
        var n=new OpportunityNegotiation { OpportunityId=id,Status=r.Status,
            ChangeSummary=r.ChangeSummary.Trim(),AgreedTerms=r.AgreedTerms,NextActionAt=r.NextActionAt,
            NegotiatedBy=CrmAccess.UserId(h),CreatedByUserId=CrmAccess.UserId(h)};
        db.OpportunityNegotiations.Add(n);await db.SaveChangesAsync(ct);
        return Results.Created("/api/crm/opportunities/"+id+"/negotiations",n);
    }
}
