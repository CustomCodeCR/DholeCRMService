namespace Dhole.Crm.Domain;

/// <summary>
/// Standalone CRM commercial benefits. Pricing does not consume these yet.
/// Miami customer classification is intentionally separate from Account.CategoryCode.
/// </summary>
public sealed class AccountBenefitSettings : CrmRecord
{
    public Guid AccountId { get; set; }
    public string? MiamiLclPlanCode { get; set; }
    public string? Notes { get; set; }
}

public sealed class AccountPreferentialRate : CrmRecord
{
    public Guid AccountId { get; set; }
    public string Modality { get; set; } = "FCL";
    // Optional lane qualifier, e.g. MIAMI for the LCL Miami product.
    public string? LaneCode { get; set; }
    public string ChargeCode { get; set; } = "FREIGHT";
    public string? CustomChargeName { get; set; }
    public string RateType { get; set; } = "FIXED_AMOUNT";
    public decimal? Value { get; set; }
    public decimal? MinimumCharge { get; set; }
    public string? CurrencyCode { get; set; }
    public string? UnitCode { get; set; }
    public string? EquipmentCode { get; set; }
    public string? OriginCode { get; set; }
    public string? DestinationCode { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}
