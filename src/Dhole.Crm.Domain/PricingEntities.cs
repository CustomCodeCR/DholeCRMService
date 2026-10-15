namespace Dhole.Crm.Domain;

/// <summary>A commercial snapshot; Pricing retains ownership of quote and status.</summary>
public sealed class OpportunityQuote : CrmRecord
{
    public Guid OpportunityId { get; set; }
    public Guid PricingQuoteId { get; set; }
    public Guid? PricingRequestId { get; set; }
    public string QuoteNumberSnapshot { get; set; } = "";
    public string CommercialStatusSnapshot { get; set; } = "";
    public string ShipmentModeSnapshot { get; set; } = "";
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;
}
/// <summary>Stable correlation from CRM opportunity to an actual Pricing request.</summary>
public sealed class OpportunityPricingRequest : CrmRecord
{
    public Guid OpportunityId { get; set; }
    public Guid PricingRequestId { get; set; }
    public Guid SellerUserId { get; set; }
    public Guid? PricingQuoteId { get; set; }
    public string StatusSnapshot { get; set; } = "";
    public string? IdempotencyKeyHash { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;
}
