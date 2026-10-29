namespace Dhole.Crm.Domain;

/// <summary>Additional company details, independent of credit and contacts.</summary>
public sealed class AccountBusinessProfile : CrmRecord
{
    public Guid AccountId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Sector { get; set; }
    public string? BusinessActivity { get; set; }
    public string? Products { get; set; }
    public string? CompanyHistory { get; set; }
    public string? BillingEmail { get; set; }
    public string? MarketingEmail { get; set; }
    public string? CommercialObservations { get; set; }
    public string? OperationalInstructions { get; set; }
}
