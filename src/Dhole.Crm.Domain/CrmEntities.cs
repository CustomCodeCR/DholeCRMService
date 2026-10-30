namespace Dhole.Crm.Domain;

/// <summary>Only references to Auth/Config. No cross-service foreign keys.</summary>
public abstract class CrmRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class Account : CrmRecord
{
    public string DisplayName { get; set; } = "";
    public string LegalName { get; set; } = "";
    public Guid? CountryId { get; set; }
    public string? TaxIdTypeCode { get; set; }
    public string? TaxId { get; set; }
    public string? NormalizedTaxId { get; set; }
    public string CustomerTypeCode { get; set; } = "cash";
    public string? SourceCode { get; set; }
    public string? RelationshipTypeCode { get; set; }
    public Guid? LogoFileId { get; set; }
    public string CategoryType { get; set; } = "CUSTOMER";
    public string CategoryCode { get; set; } = "D";
    public Guid ExecutiveUserId { get; set; }
    // Explicit institutional seller; ExecutiveUserId remains the accountable Auth user.
    public bool IsCastroFallasExecutive { get; set; }
    public string VerificationStatus { get; set; } = "Pending";
    public DateTime? HaciendaVerifiedAt { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? ArchivedAt { get; set; }
}
public sealed class Lead : CrmRecord
{
    public string Name { get; set; } = "";
    public string? CompanyName { get; set; }
    public Guid? CountryId { get; set; }
    public string? TaxId { get; set; }
    public string? SourceCode { get; set; }
    public string? RelationshipTypeCode { get; set; }
    public Guid ExecutiveUserId { get; set; }
    public string QualificationStatus { get; set; } = "New";
    public string? ContactEmail { get; set; }
    public string? Phone { get; set; }
    public DateTime? ConvertedAt { get; set; }
    public Guid? ConvertedAccountId { get; set; }
    public DateTime? ArchivedAt { get; set; }
}
public sealed class LeadConversion : CrmRecord
{
    public Guid LeadId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ConvertedBy { get; set; }
    public DateTime ConvertedAt { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
}
public sealed class AccountAssignment : CrmRecord
{
    public Guid AccountId { get; set; }
    public Guid ExecutiveUserId { get; set; }
    public bool IsCastroFallasExecutive { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public Guid AssignedBy { get; set; }
    public string? Reason { get; set; }
}
public sealed class Contact : CrmRecord
{
    // Keep Name as the derived display/search field for existing integrations.
    // Legacy records keep FirstName/LastName null until explicitly corrected.
    public string Name { get; set; } = "";
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? JobTitle { get; set; }
    public Guid? CountryId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? ArchivedAt { get; set; }
}
public sealed class AccountContact
{
    public Guid AccountId { get; set; }
    public Guid ContactId { get; set; }
    public string RelationRole { get; set; } = "OTHER";
    public bool IsPrimary { get; set; }
    public DateTime ActiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ActiveTo { get; set; }
}
public sealed class Address : CrmRecord
{
    public Guid AccountId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? CountryId { get; set; }
    public string AddressType { get; set; } = "OTHER";
    public string AddressText { get; set; } = "";
    public string? Location { get; set; }
}
public sealed class ContactPreference : CrmRecord
{
    public Guid ContactId { get; set; }
    public string PreferredChannel { get; set; } = "EMAIL";
    public int PreferredDaysMask { get; set; } = 127;
    public TimeOnly? StartLocalTime { get; set; }
    public TimeOnly? EndLocalTime { get; set; }
    public string TimezoneId { get; set; } = "America/Costa_Rica";
    public int? FrequencyDays { get; set; }
    public bool DoNotContact { get; set; }
}
public sealed class MarketingConsent : CrmRecord
{
    public Guid ContactId { get; set; }
    public Guid? AccountId { get; set; }
    public string Channel { get; set; } = "EMAIL";
    public string Purpose { get; set; } = "MARKETING";
    public string Status { get; set; } = "UNKNOWN";
    public string Source { get; set; } = "MANUAL";
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public string? EvidenceReference { get; set; }
}
public sealed class AccountBusinessActivity : CrmRecord
{
    public Guid AccountId { get; set; }
    public string ActivityCode { get; set; } = "";
    public string? ActivityDescription { get; set; }
    public string Source { get; set; } = "MANUAL";
    public DateTime? HaciendaSnapshotAt { get; set; }
}
