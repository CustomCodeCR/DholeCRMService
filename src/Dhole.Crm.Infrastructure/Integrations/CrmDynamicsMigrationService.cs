using System.Data;
using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Integrations;

public sealed record CrmDynamicsPreview(
    int Examined, int WouldCreate, int Unchanged, int Conflicts,
    bool Truncated, int MetadataVersion, IReadOnlyDictionary<string, int> Reasons);

public sealed class CrmDynamicsMigrationService(
    CrmDbContext db,
    CrmDynamicsSavedQueryClient connector,
    CrmDynamicsAccountMapper mapper,
    IConfigReferenceService config,
    IAuthReferenceService auth,
    IConfiguration configuration)
{
    public bool Enabled => configuration.GetValue("Features:CrmDynamicsSyncEnabled", false);

    private async Task<string?> ValidateAsync(CrmDynamicsAccountCandidate item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.TaxId)) return "missing_tax_identity_review";
        if (!await config.IsActiveCountryAsync(item.CountryId, ct)) return "country_not_found";
        if (await auth.GetActiveAsync(item.ExecutiveId, ct) is null) return "executive_inactive";
        return null;
    }

    private static string Tax(string? value) =>
        Regex.Replace((value ?? "").Trim().ToUpperInvariant(), @"[\s.\-]", "");

    private async Task<(string Action, Guid? ExistingId, string? Reason)> DecideAsync(
        CrmDynamicsAccountCandidate item, CancellationToken ct)
    {
        var mapping = await db.ExternalMappings.AsNoTracking().FirstOrDefaultAsync(x =>
            x.ExternalSystem == "Dynamics" && x.EntityType == "Account" &&
            x.ExternalId == item.ExternalId.ToString("D"), ct);
        if (mapping is not null)
        {
            return mapping.LastPayloadHash == item.PayloadHash
                ? ("Unchanged", mapping.EntityId, null)
                : ("Conflict", mapping.EntityId, "remote_changed_requires_review");
        }
        var tax = Tax(item.TaxId);
        if (tax.Length == 0) return ("Conflict", null, "missing_tax_identity_review");
        var duplicate = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CountryId == item.CountryId && x.TaxIdTypeCode == item.TaxIdTypeCode &&
            x.NormalizedTaxId == tax && x.ArchivedAt == null, ct);
        return duplicate is null
            ? ("Create", null, null)
            : ("Conflict", duplicate.Id, "existing_crm_identity_requires_linking");
    }

    public async Task<CrmDynamicsPreview> PreviewAsync(int maxPages, CancellationToken ct)
    {
        if (!Enabled || !mapper.Configured)
            throw new InvalidOperationException("dynamics_integration_disabled_or_mapping_missing");
        var examined = 0;
        var creates = 0;
        var unchanged = 0;
        var conflicts = 0;
        var versions = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        string? cursor = null;
        bool truncated = false;
        var seen = new HashSet<Guid>();
        var seenTax = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < Math.Clamp(maxPages, 1, 10); pageNumber++)
        {
            var page = await connector.ReadAsync(cursor, ct);
            versions = page.MetadataVersion;
            foreach (var row in page.Items)
            {
                examined++;
                var (item, error) = mapper.Parse(row);
                var reason = error;
                string action = "Conflict";
                if (item is not null)
                {
                    if (!seen.Add(item.ExternalId)) reason = "duplicate_external_record";
                    else if (!seenTax.Add(item.CountryId + ":" + item.TaxIdTypeCode + ":" + Tax(item.TaxId)))
                        reason = "duplicate_fiscal_identity_in_batch";
                    else
                    {
                        reason = await ValidateAsync(item, ct);
                        if (reason is null)
                        {
                            var decision = await DecideAsync(item, ct);
                            action = decision.Action;
                            reason = decision.Reason;
                        }
                    }
                }
                if (action == "Create" && reason is null) creates++;
                else if (action == "Unchanged" && reason is null) unchanged++;
                else
                {
                    conflicts++;
                    var code = reason ?? "review_required";
                    reasons[code] = reasons.GetValueOrDefault(code) + 1;
                }
            }
            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return new(examined, creates, unchanged, conflicts,
                false, versions, reasons);
        }
        truncated = cursor is not null;
        return new(examined, creates, unchanged, conflicts, truncated, versions, reasons);
    }

    /// <summary>
    /// Processes ONE bounded page atomically; save cursor and mappings together.
    /// On failure no cursor advances, and a retry reprocesses the same page safely.
    /// </summary>
    public async Task<bool> ProcessNextPageAsync(Guid jobId, CancellationToken ct)
    {
        if (!Enabled || !mapper.Configured) return false;
        // Fetch outside SQL transaction; repeat is safe because the job cursor is committed atomically.
        var current = await db.SyncJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (current is null || current.Status is not ("Pending" or "Running")) return false;
        if (current.ConfigurationHash != mapper.ConfigurationFingerprint())
            throw new InvalidOperationException("dynamics_mapping_changed_restart_required");
        var page = await connector.ReadAsync(current.Cursor, ct);
        // Resolve Auth/Config references before opening a SQL transaction.
        // A 100-row page must never hold PostgreSQL locks during 100 remote calls.
        var prepared = new List<(CrmDynamicsAccountCandidate? Candidate, string? Error)>();
        foreach (var raw in page.Items)
        {
            var (candidate, error) = mapper.Parse(raw);
            if (candidate is not null && error is null)
                error = await ValidateAsync(candidate, ct);
            prepared.Add((candidate, error));
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // One import writer across worker replicas. PostgreSQL advisory XACT lock is released with the transaction.
        var locked = await db.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(8025108) AS \"Value\"").SingleAsync(ct);
        if (!locked) return false;
        var job = await db.SyncJobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null || job.Status is not ("Pending" or "Running") ||
            job.Cursor != current.Cursor) return false;
        job.Status = "Running";
        job.StartedAt ??= DateTime.UtcNow;
        var seen = new HashSet<Guid>();
        var seenTax = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (candidate, initialError) in prepared)
        {
            job.ProcessedCount++;
            var error = initialError;
            Guid? existing = null;
            if (candidate is not null)
            {
                if (!seen.Add(candidate.ExternalId)) error = "duplicate_external_record";
                else if (!seenTax.Add(candidate.CountryId + ":" + candidate.TaxIdTypeCode + ":" + Tax(candidate.TaxId)))
                    error = "duplicate_fiscal_identity_in_batch";
                // Auth and Config were verified before the SQL transaction.
            }
            if (candidate is not null && error is null)
            {
                var decision = await DecideAsync(candidate, ct);
                existing = decision.ExistingId;
                error = decision.Reason;
                if (decision.Action == "Unchanged")
                {
                    job.UnchangedCount++;
                    continue;
                }
                if (decision.Action == "Create" && error is null)
                {
                    var account = new Account
                    {
                        DisplayName = candidate.DisplayName, LegalName = candidate.LegalName,
                        CountryId = candidate.CountryId, TaxId = candidate.TaxId,
                        NormalizedTaxId = Tax(candidate.TaxId),
                        TaxIdTypeCode = candidate.TaxIdTypeCode,
                        ExecutiveUserId = candidate.ExecutiveId,
                        CustomerTypeCode = candidate.CustomerType, CategoryType = candidate.CategoryType,
                        CategoryCode = candidate.CategoryCode,
                        VerificationStatus = Guid.TryParse(configuration["Crm:Hacienda:CostaRicaCountryId"], out var crId) &&
                            candidate.CountryId != crId ? "NotApplicable" : "Pending",
                        CreatedByUserId = job.CreatedByUserId
                    };
                    db.Accounts.Add(account);
                    db.AccountAssignments.Add(new AccountAssignment
                    {
                        AccountId = account.Id, ExecutiveUserId = candidate.ExecutiveId,
                        EffectiveFrom = DateTime.UtcNow, AssignedBy = job.CreatedByUserId,
                        Reason = "Initial Dynamics import", CreatedByUserId = job.CreatedByUserId
                    });
                    db.ExternalMappings.Add(new CrmExternalMapping
                    {
                        ExternalSystem = "Dynamics", EntityType = "Account",
                        ExternalId = candidate.ExternalId.ToString("D"), EntityId = account.Id,
                        ExternalVersion = candidate.ExternalVersion,
                        LastPayloadHash = candidate.PayloadHash, LastSyncedAt = DateTime.UtcNow,
                        CreatedByUserId = job.CreatedByUserId
                    });
                    job.CreatedCount++;
                    continue;
                }
            }
            job.ConflictCount++;
            var externalId = candidate?.ExternalId.ToString("D") ?? "unresolved-" + job.ProcessedCount;
            var reason = error ?? "review_required";
            if (!await db.SyncConflicts.AnyAsync(x => x.SyncJobId == job.Id &&
                    x.EntityType == "Account" && x.ExternalId == externalId &&
                    x.ReasonCode == reason, ct))
                db.SyncConflicts.Add(new CrmSyncConflict
                {
                    SyncJobId = job.Id, EntityType = "Account",
                    ExternalId = externalId, ExistingEntityId = existing,
                    ReasonCode = reason, CreatedByUserId = job.CreatedByUserId
                });
        }
        job.Cursor = page.NextCursor;
        job.UpdatedAt = DateTime.UtcNow;
        job.RowVersion++;
        if (string.IsNullOrEmpty(page.NextCursor))
        {
            job.Status = "Completed";
            job.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return !string.IsNullOrEmpty(page.NextCursor);
    }
}
