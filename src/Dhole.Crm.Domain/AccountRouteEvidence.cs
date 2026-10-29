namespace Dhole.Crm.Domain;

/// <summary>
/// Verifiable route observed during an actual CRM meeting/call or linked Pricing quote.
/// References are logical CRM relations; no cross-service FK is created for Pricing.
/// </summary>
public sealed class AccountRouteEvidence : CrmRecord
{
    public Guid AccountId { get; set; }
    public string SourceKind { get; set; } = "MEETING";
    public Guid SourceId { get; set; }
    public string PolCode { get; set; } = "";
    public string? PoeCode { get; set; }
    public string PodCode { get; set; } = "";
    public string? ServiceCode { get; set; }
    public string? Notes { get; set; }
    public DateTime ObservedAt { get; set; }
}
