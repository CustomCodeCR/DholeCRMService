using System.Security.Cryptography;
using System.Text;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Integrations;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>Administrative import only. Every operation passes the existing CRM Auth filter.</summary>
public sealed record CrmConflictResolutionRequest(long ExpectedVersion,
    string Resolution, Guid? ExistingAccountId, string Reason);

internal static class CrmDynamicsIntegrationEndpoints
{
    public static RouteGroupBuilder MapDynamicsIntegration(this RouteGroupBuilder g)
    {
        g.MapGet("/integrations/jobs", Jobs);
        g.MapGet("/integrations/jobs/{id:guid}", Job);
        g.MapGet("/integrations/jobs/{id:guid}/conflicts", Conflicts);
        g.MapPost("/integrations/conflicts/{id:guid}/resolve", Resolve);
        g.MapPost("/integrations/dynamics/dry-run", Preview);
        g.MapPost("/integrations/dynamics/run", Enqueue);
        g.MapPost("/integrations/jobs/{id:guid}/retry", Retry);
        return g;
    }

    private static bool CanRead(HttpContext h) => CrmAccess.Scope(h, "crm.integration.read");
    private static bool CanManage(HttpContext h) => CrmAccess.Scope(h, "crm.integration.manage");

    private static async Task<IResult> Jobs(CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        var jobs = await db.SyncJobs.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).Take(100).Select(x => new {
                x.Id, x.SourceSystem, x.Direction, x.EntityType, x.Status, x.DryRun,
                x.StartedAt, x.FinishedAt, x.ProcessedCount, x.CreatedCount, x.UnchangedCount,
                x.ConflictCount, x.ErrorCount, x.LastErrorCode, x.CreatedAt
            }).ToListAsync(ct);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> Job(Guid id, CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        var job = await db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return Results.NotFound();
        return Results.Ok(new {
            job.Id, job.SourceSystem, job.Direction, job.EntityType, job.Status, job.DryRun,
            job.StartedAt, job.FinishedAt, job.ProcessedCount, job.CreatedCount,
            job.UnchangedCount, job.ConflictCount, job.ErrorCount, job.LastErrorCode,
            job.CreatedAt, job.UpdatedAt
        });
    }

    private static async Task<IResult> Conflicts(Guid id, CrmDbContext db, HttpContext h,
        int? page, CancellationToken ct)
    {
        if (!CanRead(h)) return CrmAccess.Denied();
        if (!await db.SyncJobs.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
        var pageNumber = Math.Clamp(page ?? 1, 1, 100000);
        var query = db.SyncConflicts.AsNoTracking().Where(x => x.SyncJobId == id);
        return Results.Ok(new {
            total = await query.CountAsync(ct),
            page = pageNumber, pageSize = 50,
            items = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Skip((pageNumber - 1) * 50).Take(50).Select(x => new {
                    x.Id, x.EntityType, x.ExternalId, x.ExistingEntityId,
                    x.ReasonCode, x.Resolution, x.CreatedAt, x.RowVersion
                }).ToListAsync(ct)
        });
    }

    private static async Task<IResult> Resolve(Guid id, CrmConflictResolutionRequest request,
        CrmDbContext db, HttpContext h, CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 8 or > 1000 ||
            request.Resolution is not ("LinkExisting" or "Reviewed"))
            return Results.Problem(statusCode: 400, title: "Explicit reconciliation reason and action required");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (conflict is null) return Results.NotFound();
        if (conflict.Resolution != "Pending" || conflict.RowVersion != request.ExpectedVersion)
            return Results.Conflict(new { code = "conflict_already_resolved_or_stale" });
        if (request.Resolution == "LinkExisting")
        {
            // Only a duplicate identity found by the importer can be linked, never a guessed name.
            if (conflict.EntityType != "Account" ||
                conflict.ReasonCode != "existing_crm_identity_requires_linking" ||
                conflict.ExistingEntityId != request.ExistingAccountId ||
                !Guid.TryParse(conflict.ExternalId, out var externalGuid) ||
                externalGuid == Guid.Empty || request.ExistingAccountId is not Guid localId)
                return Results.Problem(statusCode: 400, title: "Only verified fiscal duplicates can be manually linked");
            if (!await db.Accounts.AsNoTracking().AnyAsync(x => x.Id == localId && x.ArchivedAt == null, ct))
                return Results.NotFound();
            if (await db.ExternalMappings.AnyAsync(x =>
                x.ExternalSystem == "Dynamics" && x.EntityType == "Account" &&
                (x.EntityId == localId || x.ExternalId == externalGuid.ToString("D")), ct))
                return Results.Conflict(new { code = "external_or_local_mapping_already_exists" });
            db.ExternalMappings.Add(new CrmExternalMapping
            {
                ExternalSystem = "Dynamics", EntityType = "Account",
                EntityId = localId, ExternalId = externalGuid.ToString("D"),
                SyncState = "LinkedManually",
                CreatedByUserId = CrmAccess.UserId(h)
            });
        }
        // Reviewed acknowledges historical conflict. It NEVER bypasses the next import.
        conflict.Resolution = request.Resolution == "LinkExisting" ? "Linked" : "Reviewed";
        conflict.ResolvedByUserId = CrmAccess.UserId(h);
        conflict.ResolvedAt = DateTime.UtcNow;
        conflict.UpdatedByUserId = CrmAccess.UserId(h);
        conflict.UpdatedAt = DateTime.UtcNow;
        conflict.RowVersion++;
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { conflict.Id, conflict.Resolution, conflict.ResolvedAt });
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { code = "reconciliation_mapping_conflict" });
        }
    }

    private static async Task<IResult> Preview(CrmDynamicsMigrationService service, HttpContext h,
        CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        try { return Results.Ok(await service.PreviewAsync(10, ct)); }
        catch (HttpRequestException) {
            return Results.Problem(statusCode: 502, title: "Dynamics connector read failed"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return Results.Problem(statusCode: 504, title: "Dynamics connector timed out"); }
        catch (InvalidOperationException) {
            return Results.Problem(statusCode: 503, title: "Dynamics mapping or connector unavailable"); }
    }

    private static async Task<IResult> Enqueue(CrmDbContext db, CrmDynamicsMigrationService service,
        CrmDynamicsAccountMapper mapper, HttpContext h, CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        if (!service.Enabled || !mapper.Configured)
            return Results.Problem(statusCode: 503, title: "Dynamics import disabled or unmapped");
        var raw = h.Request.Headers["Idempotency-Key"].ToString();
        if (raw.Length is < 8 or > 128)
            return Results.Problem(statusCode: 400, title: "Idempotency-Key required (8–128 chars)");
        var actor = CrmAccess.UserId(h);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(actor.ToString("N") + ":" + raw)));
        var existing = await db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.RequestKeyHash == hash, ct);
        if (existing is not null)
            return Results.Ok(new { existing.Id, existing.Status, replay = true });
        var job = new CrmSyncJob {
            SourceSystem = "Dynamics", Direction = "Import", EntityType = "Account",
            Status = "Pending", ConfigurationHash = mapper.ConfigurationFingerprint(),
            CreatedByUserId = actor, LastOperatorUserId = actor, RequestKeyHash = hash
        };
        db.SyncJobs.Add(job);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) {
            var duplicate = await db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.RequestKeyHash == hash, ct);
            if (duplicate is not null) return Results.Ok(new { duplicate.Id, duplicate.Status, replay = true });
            throw;
        }
        return Results.Accepted("/api/crm/integrations/jobs/" + job.Id,
            new { job.Id, job.Status, replay = false });
    }

    private static async Task<IResult> Retry(Guid id, CrmDbContext db,
        CrmDynamicsMigrationService service, CrmDynamicsAccountMapper mapper,
        HttpContext h, CancellationToken ct)
    {
        if (!CanManage(h)) return CrmAccess.Denied();
        if (!service.Enabled || !mapper.Configured)
            return Results.Problem(statusCode: 503, title: "Dynamics import disabled");
        var job = await db.SyncJobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return Results.NotFound();
        if (job.Status is not ("Failed" or "Paused"))
            return Results.Conflict(new { code = "job_not_retryable", job.Status });
        if (job.ConfigurationHash != mapper.ConfigurationFingerprint())
            return Results.Conflict(new { code = "mapping_changed_new_dry_run_required" });
        job.Status = "Pending";
        job.LastOperatorUserId = CrmAccess.UserId(h);
        job.LastErrorCode = null;
        job.UpdatedAt = DateTime.UtcNow;
        job.RowVersion++;
        await db.SaveChangesAsync(ct);
        return Results.Accepted("/api/crm/integrations/jobs/" + id,
            new { job.Id, job.Status });
    }
}
