namespace Dhole.Crm.Domain;

/// <summary>Durable, unique correlation. External GUIDs never become internal CRM primary keys.</summary>
public sealed class CrmExternalMapping : CrmRecord
{
    public string ExternalSystem { get; set; } = "Dynamics";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string ExternalId { get; set; } = "";
    public string? ExternalVersion { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastPayloadHash { get; set; }
    public string SyncState { get; set; } = "Imported";
}

public sealed class CrmSyncJob : CrmRecord
{
    public string SourceSystem { get; set; } = "Dynamics";
    public string Direction { get; set; } = "Import";
    public string EntityType { get; set; } = "Account";
    public string Status { get; set; } = "Pending";
    public bool DryRun { get; set; }
    public string? Cursor { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int ProcessedCount { get; set; }
    public int CreatedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int ConflictCount { get; set; }
    public int ErrorCount { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? ConfigurationHash { get; set; }
    public string? RequestKeyHash { get; set; }
    public Guid? LastOperatorUserId { get; set; }
}

public sealed class CrmSyncConflict : CrmRecord
{
    public Guid SyncJobId { get; set; }
    public string EntityType { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public Guid? ExistingEntityId { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Resolution { get; set; } = "Pending";
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
}

public sealed class CrmHaciendaSnapshot : CrmRecord
{
    public Guid AccountId { get; set; }
    public string ResolvedLegalName { get; set; } = "";
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "HACIENDA";
}
