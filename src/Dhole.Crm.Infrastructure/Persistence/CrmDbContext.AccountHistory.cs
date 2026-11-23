using System.Globalization;
using System.Text.Json;
using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Infrastructure.Persistence;

public sealed partial class CrmDbContext
{
    // Metadata changes are not customer data changes and must not create
    // misleading revisions. Derived normalized tax IDs are excluded as well.
    private static readonly HashSet<string> AccountAuditExcludedFields = new(StringComparer.Ordinal)
    {
        "Id", "AccountId", "CreatedByUserId", "UpdatedByUserId",
        "CreatedAt", "UpdatedAt", "RowVersion", "NormalizedTaxId",
        "CreationKeyHash", "CreationPayloadHash",
    };

    private static string? AccountAuditValue(object? value) => value switch
    {
        null => null,
        DateTime timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // Called before the business transaction is saved; audit rows commit or
    // roll back together with the account or profile changes.
    private void CaptureAccountChanges()
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries()
            .Where(e => (e.Entity is Account || e.Entity is AccountBusinessProfile || e.Entity is CreditContract) &&
                        (e.State == EntityState.Added || e.State == EntityState.Modified))
            .ToList();

        foreach (var entry in entries)
        {
            var added = entry.State == EntityState.Added;
            var record = (CrmRecord)entry.Entity;
            var isAccount = entry.Entity is Account;
            var isContract = entry.Entity is CreditContract;
            var accountId = isAccount
                ? ((Account)entry.Entity).Id
                : isContract ? ((CreditContract)entry.Entity).AccountId
                : ((AccountBusinessProfile)entry.Entity).AccountId;
            var changes = entry.Properties
                .Where(p => !AccountAuditExcludedFields.Contains(p.Metadata.Name) &&
                            (added || p.IsModified))
                .Select(p => new AccountFieldChange(
                    p.Metadata.Name,
                    added ? null : AccountAuditValue(p.OriginalValue),
                    AccountAuditValue(p.CurrentValue)))
                .Where(change => !string.Equals(change.Before, change.After, StringComparison.Ordinal))
                .ToList();
            if (changes.Count == 0) continue;

            var actor = added ? record.CreatedByUserId : record.UpdatedByUserId;
            var occurredAt = added ? record.CreatedAt : record.UpdatedAt;
            if (occurredAt.Kind != DateTimeKind.Utc) occurredAt = occurredAt.ToUniversalTime();
            var fields = changes.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);
            var action = added ? (isAccount ? "CREATED" : isContract ? "CONTRACT_CREATED" : "PROFILE_CREATED")
                : isContract ? "CONTRACT_UPDATED"
                : !isAccount ? "PROFILE_UPDATED"
                : fields.Contains("ArchivedAt") ? "ARCHIVED"
                : fields.Contains("ExecutiveUserId") || fields.Contains("IsCastroFallasExecutive") ? "ASSIGNED"
                : fields.SetEquals(new[] { "LogoFileId" }) ? "LOGO_UPDATED"
                : "UPDATED";

            AccountChangeHistories.Add(new AccountChangeHistory
            {
                AccountId = accountId,
                ActorUserId = actor is Guid userId && userId != Guid.Empty ? userId : null,
                CreatedByUserId = actor ?? Guid.Empty,
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt,
                AreaCode = isAccount ? "ACCOUNT" : isContract ? "CREDIT_CONTRACT" : "BUSINESS_PROFILE",
                ActionCode = action,
                ChangesJson = JsonSerializer.Serialize(changes,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            });
        }
    }

    // Writes are committed atomically with their business record and durable outbox.
    private void CaptureLeadChanges()
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries()
            .Where(e => (e.Entity is Lead || e.Entity is LeadAddress) &&
                        (e.State == EntityState.Added || e.State == EntityState.Modified))
            .ToList();
        foreach (var entry in entries)
        {
            var created = entry.State == EntityState.Added;
            var record = (CrmRecord)entry.Entity;
            var isLocation = entry.Entity is LeadAddress;
            var leadId = isLocation ? ((LeadAddress)entry.Entity).LeadId : ((Lead)entry.Entity).Id;
            var changes = entry.Properties
                .Where(p => !AccountAuditExcludedFields.Contains(p.Metadata.Name) &&
                            p.Metadata.Name != "LeadId" && (created || p.IsModified))
                .Select(p => new AccountFieldChange(
                    p.Metadata.Name, created ? null : AccountAuditValue(p.OriginalValue),
                    AccountAuditValue(p.CurrentValue)))
                .Where(x => !string.Equals(x.Before, x.After, StringComparison.Ordinal)).ToList();
            if (changes.Count == 0) continue;
            var actor = created ? record.CreatedByUserId : record.UpdatedByUserId;
            var occurredAt = created ? record.CreatedAt : record.UpdatedAt;
            if (occurredAt.Kind != DateTimeKind.Utc) occurredAt = occurredAt.ToUniversalTime();
            LeadChangeHistories.Add(new LeadChangeHistory
            {
                LeadId = leadId,
                ActorUserId = actor is Guid userId && userId != Guid.Empty ? userId : null,
                CreatedByUserId = actor ?? Guid.Empty,
                CreatedAt = occurredAt, UpdatedAt = occurredAt,
                AreaCode = isLocation ? "LOCATION" : "LEAD",
                ActionCode = LeadChangeAudit.Action(created, isLocation,
                    changes.Select(x => x.Field).ToHashSet(StringComparer.Ordinal)),
                ChangesJson = JsonSerializer.Serialize(changes,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            });
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CaptureAccountChanges();
        CaptureLeadChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CaptureAccountChanges();
        CaptureLeadChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
