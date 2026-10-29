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
    public string? ActiveServices { get; set; }
    public string? PresenceCountries { get; set; }
    public string? CommercialStrategy { get; set; }
    public string? ProjectsOfInterest { get; set; }
    public string? PricingObservations { get; set; }
    public string? TariffMonitoringNotes { get; set; }
    public string? TariffSendingNotes { get; set; }
    public string? CustomsInstructions { get; set; }
    public string? CargoCollectionInstructions { get; set; }
    public string? DocumentReviewInstructions { get; set; }
    public string? AssignedOperations { get; set; }
    public string? CompetitorObservations { get; set; }
    public string? TradeAgreementNotes { get; set; }
    public string? WebCustomerReference { get; set; }
}
