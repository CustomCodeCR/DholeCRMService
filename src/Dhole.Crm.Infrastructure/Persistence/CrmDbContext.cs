using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Infrastructure.Persistence;

public sealed partial class CrmDbContext(DbContextOptions<CrmDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadConversion> LeadConversions => Set<LeadConversion>();
    public DbSet<AccountAssignment> AccountAssignments => Set<AccountAssignment>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<AccountContact> AccountContacts => Set<AccountContact>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<ContactPreference> ContactPreferences => Set<ContactPreference>();
    public DbSet<MarketingConsent> MarketingConsents => Set<MarketingConsent>();
    public DbSet<AccountBusinessActivity> AccountBusinessActivities => Set<AccountBusinessActivity>();
    public DbSet<SalesPipeline> SalesPipelines => Set<SalesPipeline>();
    public DbSet<SalesPipelineStage> SalesPipelineStages => Set<SalesPipelineStage>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<OpportunityStageHistory> OpportunityStageHistories => Set<OpportunityStageHistory>();
    public DbSet<OpportunityContact> OpportunityContacts => Set<OpportunityContact>();
    public DbSet<OpportunityNegotiation> OpportunityNegotiations => Set<OpportunityNegotiation>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ActivityParticipant> ActivityParticipants => Set<ActivityParticipant>();
    public DbSet<ActivityNote> ActivityNotes => Set<ActivityNote>();
    public DbSet<AccountRouteEvidence> AccountRouteEvidence => Set<AccountRouteEvidence>();
    public DbSet<AccountBusinessProfile> AccountBusinessProfiles => Set<AccountBusinessProfile>();
    public DbSet<CommercialAnalysis> CommercialAnalyses => Set<CommercialAnalysis>();
    public DbSet<CommercialAnalysisRoute> CommercialAnalysisRoutes => Set<CommercialAnalysisRoute>();
    public DbSet<CommercialAnalysisService> CommercialAnalysisServices => Set<CommercialAnalysisService>();
    public DbSet<CustomerTradeProfile> CustomerTradeProfiles => Set<CustomerTradeProfile>();
    public DbSet<OpportunityQuote> OpportunityQuotes => Set<OpportunityQuote>();
    public DbSet<OpportunityPricingRequest> OpportunityPricingRequests => Set<OpportunityPricingRequest>();
    public DbSet<AccountFollowupPolicy> AccountFollowupPolicies => Set<AccountFollowupPolicy>();
    public DbSet<Followup> Followups => Set<Followup>();
    public DbSet<FollowupHistory> FollowupHistories => Set<FollowupHistory>();
    public DbSet<CrmReminder> Reminders => Set<CrmReminder>();
    public DbSet<CrmOutboxMessage> OutboxMessages => Set<CrmOutboxMessage>();
    public DbSet<CrmInboxMessage> InboxMessages => Set<CrmInboxMessage>();

    public DbSet<CreditProfile> CreditProfiles => Set<CreditProfile>();
    public DbSet<CreditCoverage> CreditCoverages => Set<CreditCoverage>();
    public DbSet<CreditContract> CreditContracts => Set<CreditContract>();
    public DbSet<CreditTermsHistory> CreditTermsHistory => Set<CreditTermsHistory>();
    public DbSet<CreditContractRenewal> CreditContractRenewals => Set<CreditContractRenewal>();
    public DbSet<CreditContractAmendment> CreditContractAmendments => Set<CreditContractAmendment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("crm");
        ConfigureRecord<Account>(b, "crm_accounts");
        ConfigureRecord<Lead>(b, "crm_leads");
        ConfigureRecord<LeadConversion>(b, "crm_lead_conversions");
        ConfigureRecord<AccountAssignment>(b, "crm_account_assignments");
        ConfigureRecord<Contact>(b, "crm_contacts");
        ConfigureRecord<Address>(b, "crm_addresses");
        ConfigureRecord<ContactPreference>(b, "crm_contact_preferences");
        ConfigureRecord<MarketingConsent>(b, "crm_marketing_consents");
        ConfigureRecord<AccountBusinessActivity>(b, "crm_account_business_activities");
        b.Entity<AccountContact>(e =>
        {
            e.ToTable("crm_account_contacts");
            e.HasKey(x => new { x.AccountId, x.ContactId });
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ContactId);
        });

        b.Entity<Account>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(220).IsRequired();
            e.Property(x => x.LegalName).HasMaxLength(220).IsRequired();
            e.Property(x => x.NormalizedTaxId).HasMaxLength(80);
            e.Property(x => x.TaxId).HasMaxLength(80);
            e.Property(x => x.CustomerTypeCode).HasMaxLength(10).IsRequired();
            e.Property(x => x.CategoryType).HasMaxLength(12);
            e.Property(x => x.CategoryCode).HasMaxLength(1);
            e.HasIndex(x => new { x.CountryId, x.TaxIdTypeCode, x.NormalizedTaxId }).IsUnique()
              .HasFilter("normalized_tax_id IS NOT NULL AND archived_at IS NULL");
            e.HasIndex(x => x.ExecutiveUserId);
        });
        b.Entity<Lead>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(220);
            e.Property(x => x.ContactEmail).HasMaxLength(254);
            e.HasIndex(x => x.ExecutiveUserId);
            e.HasIndex(x => x.ConvertedAccountId);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ConvertedAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LeadConversion>(e =>
        {
            e.HasIndex(x => x.LeadId).IsUnique();
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccountAssignment>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AccountId).IsUnique().HasFilter("effective_to IS NULL");
        });
        b.Entity<Contact>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(220);
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.Email);
        });
        b.Entity<Address>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ContactPreference>(e =>
        {
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ContactId).IsUnique();
        });
        b.Entity<MarketingConsent>(e =>
        {
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ContactId, x.Channel, x.Purpose, x.CapturedAt });
        });
        b.Entity<AccountBusinessActivity>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AccountId, x.ActivityCode }).IsUnique();
        });
        ConfigureCommercialModel(b);
        ConfigureFollowups(b);
        ConfigurePricingModel(b);
        ConfigureFinance(b);
        foreach (var t in b.Model.GetEntityTypes())
        {
            foreach (var prop in t.GetProperties())
                prop.SetColumnName(ToSnake(prop.Name));
            foreach (var key in t.GetKeys()) key.SetName("pk_" + t.GetTableName() + "_" + string.Join("_", key.Properties.Select(p => ToSnake(p.Name))));
            foreach (var idx in t.GetIndexes())
                idx.SetDatabaseName("ix_" + t.GetTableName() + "_" + string.Join("_", idx.Properties.Select(p => ToSnake(p.Name))));
        }
    }
    private static void ConfigureRecord<T>(ModelBuilder b, string name) where T : CrmRecord
    {
        b.Entity<T>(e =>
        {
            e.ToTable(name);
            e.HasKey(x => x.Id);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        });
    }
    private static string ToSnake(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
