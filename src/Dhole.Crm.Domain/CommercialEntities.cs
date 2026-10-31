namespace Dhole.Crm.Domain;

// Fase 3 commercial entities. All Auth/Config/Pricing identifiers remain logical references.
public sealed class SalesPipeline : CrmRecord
{
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
public sealed class SalesPipelineStage : CrmRecord
{
    public Guid PipelineId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public decimal DefaultWinProbability { get; set; }
    public bool IsWon { get; set; }
    public bool IsLost { get; set; }
    public bool IsActive { get; set; } = true;
}
public sealed class Opportunity : CrmRecord
{
    public Guid? AccountId { get; set; }
    public Guid? LeadId { get; set; }
    public string Title { get; set; } = "";
    public Guid ExecutiveUserId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid StageId { get; set; }
    public decimal AmountEstimated { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public decimal ProbabilityPct { get; set; }
    public DateTime? ExpectedCloseAt { get; set; }
    public DateTime? ActualCloseAt { get; set; }
    public string Status { get; set; } = "Open";
    public string? LossReasonCode { get; set; }
    public string? Description { get; set; }
    public DateTime? ArchivedAt { get; set; }
}
public sealed class OpportunityStageHistory : CrmRecord
{
    public Guid OpportunityId { get; set; }
    public Guid? FromStageId { get; set; }
    public Guid ToStageId { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string? Reason { get; set; }
}
public sealed class OpportunityContact
{
    public Guid OpportunityId { get; set; }
    public Guid ContactId { get; set; }
    public string DecisionRole { get; set; } = "OTHER";
}
public sealed class OpportunityNegotiation : CrmRecord
{
    public Guid OpportunityId { get; set; }
    public Guid? PricingQuoteId { get; set; } // Phase 4 will resolve the logical Pricing reference.
    public Guid NegotiatedBy { get; set; }
    public string Status { get; set; } = "Open";
    public string ChangeSummary { get; set; } = "";
    public string? AgreedTerms { get; set; }
    public DateTime? NextActionAt { get; set; }
}
public sealed class Activity : CrmRecord
{
    public Guid? AccountId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Type { get; set; } = "OTHER";
    public string Subject { get; set; } = "";
    public DateTime? ScheduledStartAt { get; set; }
    public DateTime? ScheduledEndAt { get; set; }
    public DateTime? OccurredAt { get; set; }
    public string Status { get; set; } = "Scheduled";
    public string? Outcome { get; set; }
    public string? LocationText { get; set; }
    // Individual reminder settings; the account policy remains the fallback.
    public int? ReminderLeadMinutes { get; set; }
    public string? ReminderChannelStrategy { get; set; }
    public DateTime? ArchivedAt { get; set; }
}
public sealed class ActivityParticipant : CrmRecord
{
    public Guid ActivityId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? UserId { get; set; }
    public string? ParticipantName { get; set; }
    public string Role { get; set; } = "ATTENDEE";
}
public sealed class ActivityNote : CrmRecord
{
    public Guid ActivityId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string NoteText { get; set; } = "";
}
public sealed class CommercialAnalysis : CrmRecord
{
    public Guid? AccountId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string MarketCode { get; set; } = "OTHER";
    public bool CustomsNationalizationRequired { get; set; }
    public string? Observations { get; set; }
    public decimal? EstimatedMonthlyVolume { get; set; }
    public string? VolumeUnit { get; set; }
    public DateTime? ReviewedAt { get; set; }
}
public sealed class CommercialAnalysisRoute : CrmRecord
{
    public Guid AnalysisId { get; set; }
    public string? PolCode { get; set; }
    public string? PoeCode { get; set; }
    public string? PodCode { get; set; }
    public string? OriginCountryCode { get; set; }
    public string? DestinationCountryCode { get; set; }
    public decimal? FrequencyPerMonth { get; set; }
    public string? Notes { get; set; }
}
public sealed class CommercialAnalysisService : CrmRecord
{
    public Guid AnalysisId { get; set; }
    public string ServiceCode { get; set; } = "";
    public string ModalityCode { get; set; } = "";
    public string TransportModeCode { get; set; } = "";
    public bool RequiresCustoms { get; set; }
    public string? CargoTypeCode { get; set; }
    public decimal Volume { get; set; }
    public string VolumeUnit { get; set; } = "CBM";
    public string? ContainerTypeCode { get; set; }
    public int? EquipmentQuantity { get; set; }
    public decimal? ExpectedMovementsPerMonth { get; set; }
    public string? Notes { get; set; }
}
public sealed class CustomerTradeProfile : CrmRecord
{
    public Guid AccountId { get; set; }
    public string ServiceCode { get; set; } = "";
    public string? PreferredOriginCode { get; set; }
    public string? PreferredDestinationCode { get; set; }
    public string Frequency { get; set; } = "MONTHLY";
    public decimal ExpectedVolume { get; set; }
    public string VolumeUnit { get; set; } = "CBM";
    public bool IsActive { get; set; } = true;
}
