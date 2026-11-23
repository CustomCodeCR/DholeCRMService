namespace Dhole.Crm.Domain;

/// <summary>Immutable PostgreSQL audit events for prospect records and their relations.</summary>
public sealed class LeadChangeHistory : CrmRecord
{
    public Guid LeadId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActionCode { get; set; } = "";
    public string AreaCode { get; set; } = "";
    public string ChangesJson { get; set; } = "[]";
}

public static class LeadChangeAudit
{
    public static string Action(bool created, bool location, IReadOnlyCollection<string> fields)
    {
        if (location) return created ? "LOCATION_CREATED" : "LOCATION_UPDATED";
        if (created) return "CREATED";
        if (fields.Contains("ConvertedAt") || fields.Contains("ConvertedAccountId")) return "CONVERTED";
        if (fields.Contains("ArchivedAt")) return "ARCHIVED";
        if (fields.Contains("ExecutiveUserId") || fields.Contains("IsCastroFallasExecutive")) return "ASSIGNED";
        return "UPDATED";
    }
}
