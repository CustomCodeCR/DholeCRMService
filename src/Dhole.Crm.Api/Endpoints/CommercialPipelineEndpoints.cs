using Dhole.Crm.Application.Commercial;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

public sealed record CreatePipelineRequest(string Name, bool IsDefault, int SortOrder);
public sealed record PatchPipelineRequest(long ExpectedVersion, string Name, bool IsDefault, bool IsActive, int SortOrder);
public sealed record CreatePipelineStageRequest(string Name, int SortOrder, decimal DefaultWinProbability, bool IsWon, bool IsLost);
public sealed record PatchPipelineStageRequest(long ExpectedVersion, string Name, int SortOrder,
    decimal DefaultWinProbability, bool IsWon, bool IsLost, bool IsActive);

internal static class CommercialPipelineEndpoints
{
    public static RouteGroupBuilder MapCommercialPipeline(this RouteGroupBuilder group)
    {
        group.MapGet("/pipelines", List);
        group.MapGet("/pipelines/{id:guid}/stages", Stages);
        group.MapPost("/pipelines", Create);
        group.MapPatch("/pipelines/{id:guid}", Patch);
        group.MapPost("/pipelines/{id:guid}/stages", CreateStage);
        group.MapPatch("/pipelines/{id:guid}/stages/{stageId:guid}", PatchStage);
        return group;
    }
    private static bool CanRead(HttpContext h) =>
        CrmDataEndpoints.HasRead(h,"opportunity") || CrmAccess.Scope(h,"crm.settings.manage");
    private static bool CanManage(HttpContext h) => CrmAccess.Scope(h,"crm.settings.manage");
    private static bool GoodName(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 120;
    private static async Task<IResult> List(CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        return Results.Ok(await db.SalesPipelines.AsNoTracking().OrderBy(x=>x.SortOrder)
            .ThenBy(x=>x.Name).ToListAsync(ct));
    }
    private static async Task<IResult> Stages(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        if (!await db.SalesPipelines.AnyAsync(p=>p.Id==id,ct)) return Results.NotFound();
        return Results.Ok(await db.SalesPipelineStages.AsNoTracking().Where(x=>x.PipelineId==id)
            .OrderBy(x=>x.SortOrder).ToListAsync(ct));
    }
    private static async Task<IResult> Create(CreatePipelineRequest r, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        if (!GoodName(r.Name) || r.SortOrder < 0) return CrmDataEndpoints.Bad("Invalid pipeline");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (r.IsDefault)
        {
            var previous = await db.SalesPipelines.Where(x=>x.IsDefault).ToListAsync(ct);
            foreach(var item in previous)
            {
                item.IsDefault=false; item.RowVersion++; item.UpdatedByUserId=CrmAccess.UserId(h);
                item.UpdatedAt=DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        var pipeline = new SalesPipeline { Name=r.Name.Trim(), IsDefault=r.IsDefault,
            SortOrder=r.SortOrder, IsActive=true, CreatedByUserId=CrmAccess.UserId(h) };
        db.SalesPipelines.Add(pipeline);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Created("/api/crm/pipelines/"+pipeline.Id,pipeline); }
        catch(DbUpdateException) { return CrmDataEndpoints.Conflict("Pipeline name or default constraint"); }
    }
    private static async Task<IResult> Patch(Guid id, PatchPipelineRequest r, CrmDbContext db,
        HttpContext h, CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        var p=await db.SalesPipelines.FindAsync([id],ct);
        if(p is null) return Results.NotFound();
        if(p.RowVersion!=r.ExpectedVersion) return CrmDataEndpoints.Conflict("Stale pipeline version");
        if(!GoodName(r.Name) || r.SortOrder<0 || (r.IsDefault && !r.IsActive))
            return CrmDataEndpoints.Bad("Invalid pipeline state");
        if(!r.IsActive && await db.Opportunities.AnyAsync(x=>x.PipelineId==id && x.ArchivedAt==null && x.Status=="Open",ct))
            return CrmDataEndpoints.Conflict("Pipeline still has open opportunities");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        if(r.IsDefault && !p.IsDefault)
        {
            var defaults=await db.SalesPipelines.Where(x=>x.IsDefault && x.Id!=id).ToListAsync(ct);
            foreach(var other in defaults)
            {
                other.IsDefault=false; other.RowVersion++; other.UpdatedAt=DateTime.UtcNow;
                other.UpdatedByUserId=CrmAccess.UserId(h);
            }
            await db.SaveChangesAsync(ct);
        }
        p.Name=r.Name.Trim();p.IsDefault=r.IsDefault;p.IsActive=r.IsActive;p.SortOrder=r.SortOrder;
        p.RowVersion++;p.UpdatedAt=DateTime.UtcNow;p.UpdatedByUserId=CrmAccess.UserId(h);
        try {await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(p);}
        catch(DbUpdateException){return CrmDataEndpoints.Conflict("Pipeline state conflicts with another record");}
    }
    private static async Task<IResult> CreateStage(Guid id, CreatePipelineStageRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if(!CanManage(h))return CrmAccess.Denied();
        if(!await db.SalesPipelines.AnyAsync(x=>x.Id==id && x.IsActive,ct))return Results.NotFound();
        if(!GoodName(r.Name)||r.SortOrder<0||!CommercialRules.ValidStage(r.DefaultWinProbability,r.IsWon,r.IsLost))
            return CrmDataEndpoints.Bad("Invalid pipeline stage");
        var s=new SalesPipelineStage {PipelineId=id,Name=r.Name.Trim(),SortOrder=r.SortOrder,
            DefaultWinProbability=r.DefaultWinProbability,IsWon=r.IsWon,IsLost=r.IsLost,CreatedByUserId=CrmAccess.UserId(h)};
        db.SalesPipelineStages.Add(s);
        try {await db.SaveChangesAsync(ct);return Results.Created("/api/crm/pipelines/"+id+"/stages/"+s.Id,s);}
        catch(DbUpdateException){return CrmDataEndpoints.Conflict("Duplicated pipeline stage order");}
    }
    private static async Task<IResult> PatchStage(Guid id, Guid stageId, PatchPipelineStageRequest r,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if(!CanManage(h))return CrmAccess.Denied();
        var s=await db.SalesPipelineStages.FirstOrDefaultAsync(x=>x.Id==stageId && x.PipelineId==id,ct);
        if(s is null)return Results.NotFound();
        if(s.RowVersion!=r.ExpectedVersion)return CrmDataEndpoints.Conflict("Stale stage version");
        if(!GoodName(r.Name)||r.SortOrder<0||!CommercialRules.ValidStage(r.DefaultWinProbability,r.IsWon,r.IsLost))
            return CrmDataEndpoints.Bad("Invalid stage");
        if(!r.IsActive && await db.Opportunities.AnyAsync(x=>x.StageId==stageId && x.Status=="Open",ct))
            return CrmDataEndpoints.Conflict("Stage is in use");
        s.Name=r.Name.Trim();s.SortOrder=r.SortOrder;s.IsActive=r.IsActive;
        s.DefaultWinProbability=r.DefaultWinProbability;s.IsWon=r.IsWon;s.IsLost=r.IsLost;
        s.RowVersion++;s.UpdatedAt=DateTime.UtcNow;s.UpdatedByUserId=CrmAccess.UserId(h);
        var result=await CommercialAccess.Save(db,ct);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok ? Results.Ok(s):result;
    }
}
