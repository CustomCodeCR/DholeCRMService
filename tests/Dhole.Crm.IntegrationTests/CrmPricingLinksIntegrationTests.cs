using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;
[TestClass]
public sealed class CrmPricingLinksIntegrationTests
{
    private static async Task<CrmDbContext> OpenAsync()
    {
        var cs=Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(cs)) Assert.Inconclusive("Real PostgreSQL required");
        var db=new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return db;
    }
    [TestMethod]
    public async Task Migration_adds_real_pricing_link_tables_without_touching_pricing_data()
    {
        await using var db=await OpenAsync();
        var tables=await db.Database.SqlQueryRaw<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema='crm'").ToListAsync();
        CollectionAssert.Contains(tables,"crm_opportunity_quotes");
        CollectionAssert.Contains(tables,"crm_opportunity_pricing_requests");
        CollectionAssert.Contains((await db.Database.GetAppliedMigrationsAsync()).ToList(),
            "20261014220000_AddPricingCommercialLinks");
    }
    [TestMethod]
    public async Task Multiple_quotes_for_one_opportunity_and_one_real_request_are_persisted()
    {
        await using var db=await OpenAsync();
        var actor=Guid.NewGuid();
        var lead=new Lead {Name="Quote links integration",ExecutiveUserId=actor,CreatedByUserId=actor};
        db.Leads.Add(lead);
        var stage=await db.SalesPipelineStages.AsNoTracking().OrderBy(x=>x.SortOrder).FirstAsync();
        var opportunity=new Opportunity {LeadId=lead.Id,Title="Multiple shipments",
            ExecutiveUserId=actor,PipelineId=stage.PipelineId,StageId=stage.Id,
            CurrencyCode="USD",AmountEstimated=100,CreatedByUserId=actor};
        db.Opportunities.Add(opportunity);
        var request=Guid.NewGuid();
        db.OpportunityPricingRequests.Add(new OpportunityPricingRequest {OpportunityId=opportunity.Id,
            PricingRequestId=request,SellerUserId=actor,StatusSnapshot="Open",CreatedByUserId=actor,
            IdempotencyKeyHash=Guid.NewGuid().ToString("N"),RequestPayloadHash=Guid.NewGuid().ToString("N")});
        var quote1=Guid.NewGuid();var quote2=Guid.NewGuid();
        db.OpportunityQuotes.Add(new OpportunityQuote {OpportunityId=opportunity.Id,
            PricingQuoteId=quote1,PricingRequestId=request,QuoteNumberSnapshot="QUO-TEST-1",
            CommercialStatusSnapshot="Sent",ShipmentModeSnapshot="FCL",CreatedByUserId=actor});
        db.OpportunityQuotes.Add(new OpportunityQuote {OpportunityId=opportunity.Id,
            PricingQuoteId=quote2,QuoteNumberSnapshot="QUO-TEST-2",
            CommercialStatusSnapshot="AcceptedByClient",ShipmentModeSnapshot="LCL",CreatedByUserId=actor});
        await db.SaveChangesAsync();
        Assert.AreEqual(2,await db.OpportunityQuotes.CountAsync(x=>x.OpportunityId==opportunity.Id));
        Assert.AreEqual(1,await db.OpportunityPricingRequests.CountAsync(x=>x.OpportunityId==opportunity.Id));
        var stored=await db.Opportunities.AsNoTracking().SingleAsync(x=>x.Id==opportunity.Id);
        Assert.AreEqual("Open",stored.Status,
            "Pricing acceptance must never auto-close opportunity without an explicit CRM decision");
    }
    [TestMethod]
    public async Task Database_rejects_duplicate_quote_link_and_request_reuse()
    {
        await using var db=await OpenAsync();
        var actor=Guid.NewGuid();
        var lead=new Lead {Name="CRM PR unique",ExecutiveUserId=actor,CreatedByUserId=actor};
        db.Leads.Add(lead);
        var stage=await db.SalesPipelineStages.AsNoTracking().OrderBy(x=>x.SortOrder).FirstAsync();
        var opportunity=new Opportunity {LeadId=lead.Id,Title="Unique quote test",
            ExecutiveUserId=actor,PipelineId=stage.PipelineId,StageId=stage.Id,
            CurrencyCode="USD",AmountEstimated=100,CreatedByUserId=actor};
        db.Opportunities.Add(opportunity);
        var quoteId=Guid.NewGuid();
        db.OpportunityQuotes.Add(new OpportunityQuote {OpportunityId=opportunity.Id,
            PricingQuoteId=quoteId,QuoteNumberSnapshot="QUO-1",
            CommercialStatusSnapshot="Sent",CreatedByUserId=actor});
        await db.SaveChangesAsync();
        db.OpportunityQuotes.Add(new OpportunityQuote {OpportunityId=opportunity.Id,
            PricingQuoteId=quoteId,QuoteNumberSnapshot="QUO-2",
            CommercialStatusSnapshot="Sent",CreatedByUserId=actor});
        var rejected=false;
        try {await db.SaveChangesAsync();}
        catch(DbUpdateException) {rejected=true;}
        Assert.IsTrue(rejected,"SQL unique constraint must prevent duplicate opportunity quote links");
    }
}
