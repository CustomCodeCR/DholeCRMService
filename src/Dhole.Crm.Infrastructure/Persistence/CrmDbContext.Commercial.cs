using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Infrastructure.Persistence;

public sealed partial class CrmDbContext
{
    private static void ConfigurePricingModel(ModelBuilder b)
    {
        ConfigureRecord<OpportunityQuote>(b,"crm_opportunity_quotes");
        ConfigureRecord<OpportunityPricingRequest>(b,"crm_opportunity_pricing_requests");
        b.Entity<OpportunityQuote>(e =>
        {
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x=>x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x=>new { x.OpportunityId,x.PricingQuoteId }).IsUnique();
            e.HasIndex(x=>x.PricingRequestId);
            e.Property(x=>x.QuoteNumberSnapshot).HasMaxLength(100);
            e.Property(x=>x.CommercialStatusSnapshot).HasMaxLength(50);
        });
        b.Entity<OpportunityPricingRequest>(e =>
        {
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x=>x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x=>new { x.OpportunityId,x.PricingRequestId }).IsUnique();
            e.HasIndex(x=>x.PricingRequestId).IsUnique();
            e.HasIndex(x=>new { x.OpportunityId,x.IdempotencyKeyHash }).IsUnique()
              .HasFilter("idempotency_key_hash IS NOT NULL");
            e.Property(x=>x.StatusSnapshot).HasMaxLength(50);
            e.Property(x=>x.IdempotencyKeyHash).HasMaxLength(64);
            e.Property(x=>x.RequestPayloadHash).HasMaxLength(64);
        });
    }

    private static void ConfigureCommercialModel(ModelBuilder b)
    {
        ConfigureRecord<SalesPipeline>(b, "crm_sales_pipelines");
        ConfigureRecord<SalesPipelineStage>(b, "crm_sales_pipeline_stages");
        ConfigureRecord<Opportunity>(b, "crm_opportunities");
        ConfigureRecord<OpportunityStageHistory>(b, "crm_opportunity_stage_history");
        ConfigureRecord<OpportunityNegotiation>(b, "crm_opportunity_negotiations");
        ConfigureRecord<Activity>(b, "crm_activities");
        ConfigureRecord<ActivityParticipant>(b, "crm_activity_participants");
        ConfigureRecord<ActivityNote>(b, "crm_activity_notes");
        ConfigureRecord<AccountBusinessProfile>(b, "crm_account_business_profiles");
        b.Entity<AccountBusinessProfile>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AccountId).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(60);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Website).HasMaxLength(500);
            e.Property(x => x.Sector).HasMaxLength(350);
            e.Property(x => x.BusinessActivity).HasMaxLength(2000);
            e.Property(x => x.Products).HasMaxLength(2000);
            e.Property(x => x.CompanyHistory).HasMaxLength(4000);
            e.Property(x => x.BillingEmail).HasMaxLength(254);
            e.Property(x => x.MarketingEmail).HasMaxLength(254);
            e.Property(x => x.CommercialObservations).HasMaxLength(4000);
            e.Property(x => x.OperationalInstructions).HasMaxLength(4000);
        });
        ConfigureRecord<AccountRouteEvidence>(b, "crm_account_route_evidence");
        b.Entity<AccountRouteEvidence>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.PolCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.PoeCode).HasMaxLength(80);
            e.Property(x => x.PodCode).HasMaxLength(80).IsRequired();
            e.Property(x => x.ServiceCode).HasMaxLength(80);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => new { x.AccountId, x.ObservedAt });
        });
        ConfigureRecord<CommercialAnalysis>(b, "crm_commercial_analyses");
        ConfigureRecord<CommercialAnalysisRoute>(b, "crm_commercial_analysis_routes");
        ConfigureRecord<CommercialAnalysisService>(b, "crm_commercial_analysis_services");
        ConfigureRecord<CustomerTradeProfile>(b, "crm_customer_trade_profiles");

        b.Entity<SalesPipeline>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.IsDefault).IsUnique().HasFilter("is_default AND is_active");
        });
        b.Entity<SalesPipelineStage>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.DefaultWinProbability).HasPrecision(7, 4);
            e.HasOne<SalesPipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new {x.PipelineId, x.Id}).IsUnique();
            e.HasIndex(x => new {x.PipelineId, x.SortOrder}).IsUnique();
        });
        b.Entity<Opportunity>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(250).IsRequired();
            e.Property(x => x.AmountEstimated).HasPrecision(18, 2);
            e.Property(x => x.ProbabilityPct).HasPrecision(7, 4);
            e.Property(x => x.CurrencyCode).HasMaxLength(12);
            e.Property(x => x.Status).HasMaxLength(30);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SalesPipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SalesPipelineStage>().WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new {x.ExecutiveUserId,x.Status,x.ExpectedCloseAt});
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => x.LeadId);
        });
        b.Entity<OpportunityStageHistory>(e =>
        {
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SalesPipelineStage>().WithMany().HasForeignKey(x => x.ToStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SalesPipelineStage>().WithMany().HasForeignKey(x => x.FromStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new {x.OpportunityId,x.ChangedAt});
        });
        b.Entity<OpportunityContact>(e =>
        {
            e.ToTable("crm_opportunity_contacts");
            e.HasKey(x => new {x.OpportunityId,x.ContactId});
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DecisionRole).HasMaxLength(60);
        });
        b.Entity<OpportunityNegotiation>(e =>
        {
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new {x.OpportunityId,x.CreatedAt});
        });
        b.Entity<Activity>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(24);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.Subject).HasMaxLength(250);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new {x.AccountId,x.OccurredAt});
            e.HasIndex(x => new {x.OwnerUserId,x.Status,x.ScheduledStartAt});
        });
        b.Entity<ActivityParticipant>(e =>
        {
            e.HasOne<Activity>().WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ActivityId);
        });
        b.Entity<ActivityNote>(e =>
        {
            e.HasOne<Activity>().WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.NoteText).HasMaxLength(8000);
            e.HasIndex(x => new {x.ActivityId,x.CreatedAt});
        });
        b.Entity<CommercialAnalysis>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.EstimatedMonthlyVolume).HasPrecision(18,4);
            e.HasIndex(x => new {x.OwnerUserId,x.CreatedAt});
        });
        b.Entity<CommercialAnalysisRoute>(e =>
        {
            e.HasOne<CommercialAnalysis>().WithMany().HasForeignKey(x => x.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.FrequencyPerMonth).HasPrecision(18,4);
            e.HasIndex(x => x.AnalysisId);
        });
        b.Entity<CommercialAnalysisService>(e =>
        {
            e.HasOne<CommercialAnalysis>().WithMany().HasForeignKey(x => x.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Volume).HasPrecision(18,4);
            e.Property(x => x.ExpectedMovementsPerMonth).HasPrecision(18,4);
            e.HasIndex(x => x.AnalysisId);
        });
        b.Entity<CustomerTradeProfile>(e =>
        {
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ExpectedVolume).HasPrecision(18,4);
            e.HasIndex(x => new {x.AccountId,x.ServiceCode,x.Frequency});
        });
    }
}
