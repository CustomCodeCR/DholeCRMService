namespace Dhole.Crm.Domain;

// Financial data is owned by CRM and is never used to infer an accounts-receivable balance.
public sealed class CreditProfile : CrmRecord
{
    public Guid AccountId { get; set; }
    public bool CreditEnabled { get; set; }
    public int CreditDays { get; set; }
    public decimal CreditLimit { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    // Percent units: 2.5000 means 2.5%, NOT 0.025.
    public decimal MonthlyFinancingPct { get; set; }
    public string PaymentConditions { get; set; } = "";
    public string Status { get; set; } = "PendingApproval";
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewAt { get; set; }
}
public sealed class CreditCoverage : CrmRecord
{
    public Guid CreditProfileId { get; set; }
    public string ServiceCode { get; set; } = "";
    public string CoverageType { get; set; } = "INCLUDED";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}
public sealed class CreditContract : CrmRecord
{
    public Guid AccountId { get; set; }
    public Guid? CreditProfileId { get; set; }
    public string? ContractNumber { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime RenewalDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string PaymentTerms { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public bool AutoRenew { get; set; }
    public Guid? SignedDocumentFileId { get; set; }
}
public sealed class CreditTermsHistory : CrmRecord
{
    public Guid CreditProfileId { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string OldValuesJson { get; set; } = "{}";
    public string NewValuesJson { get; set; } = "{}";
    public string ApprovalReason { get; set; } = "";
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
}
public sealed class CreditContractRenewal : CrmRecord
{
    public Guid? OperationId { get; set; }
    public Guid ContractId { get; set; }
    public Guid ChangedBy { get; set; }
    public DateTime PreviousRenewalDate { get; set; }
    public DateTime NewRenewalDate { get; set; }
    public DateTime? PreviousExpiryDate { get; set; }
    public DateTime? NewExpiryDate { get; set; }
    public string Reason { get; set; } = "";
}
