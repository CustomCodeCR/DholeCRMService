using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;
[TestClass]
public sealed class CrmCommercialIntegrationTests
{
    private static async Task<CrmDbContext> Open()
    {
        var cs=Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(cs))Assert.Inconclusive("A real PostgreSQL database is mandatory");
        var db=new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return db;
    }
    [TestMethod]
    public async Task Commercial_migration_installs_all_thirteen_tables_and_default_stages()
    {
        await using var db=await Open();
        var tables=await db.Database.SqlQueryRaw<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema='crm'").ToListAsync();
        foreach(var name in new[]{
            "crm_sales_pipelines","crm_sales_pipeline_stages","crm_opportunities",
            "crm_opportunity_stage_history","crm_opportunity_contacts","crm_opportunity_negotiations",
            "crm_activities","crm_activity_participants","crm_activity_notes",
            "crm_commercial_analyses","crm_commercial_analysis_routes",
            "crm_commercial_analysis_services","crm_customer_trade_profiles"})
            CollectionAssert.Contains(tables,name);
        Assert.AreEqual(6, await db.SalesPipelineStages.CountAsync());
        Assert.AreEqual(1, await db.SalesPipelines.CountAsync(x=>x.IsDefault));
        var stages=await db.SalesPipelineStages.ToListAsync();
        Assert.AreEqual(1,stages.Count(x=>x.IsWon));
        Assert.AreEqual(1,stages.Count(x=>x.IsLost));
        var applied=(await db.Database.GetAppliedMigrationsAsync()).ToList();
        CollectionAssert.Contains(applied,"20261014210000_AddCommercialPipelineActivitiesAnalysis");
    }
    [TestMethod]
    public async Task Route_evidence_migration_persists_meeting_source_and_rejects_duplicate_ports()
    {
        await using var db = await Open();
        var table = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name::text AS \"Value\" FROM information_schema.tables WHERE table_schema='crm' AND table_name='crm_account_route_evidence'")
            .SingleAsync();
        Assert.AreEqual("crm_account_route_evidence", table);
        var user = Guid.NewGuid();
        var account = new Account { DisplayName = "Route customer", LegalName = "Route customer",
            ExecutiveUserId = user, CategoryCode = "D", CreatedByUserId = user };
        db.Accounts.Add(account);
        var meeting = new Activity { AccountId = account.Id, OwnerUserId = user,
            Type = "VISIT", Subject = "Shipping meeting", Status = "Completed",
            OccurredAt = DateTime.UtcNow, CreatedByUserId = user };
        db.Activities.Add(meeting);
        var recorded = new AccountRouteEvidence { AccountId = account.Id,
            SourceKind = "MEETING", SourceId = meeting.Id, PolCode = "CNSHA",
            PoeCode = "PAMNZ", PodCode = "CRCAL", ObservedAt = DateTime.UtcNow,
            CreatedByUserId = user };
        db.AccountRouteEvidence.Add(recorded);
        await db.SaveChangesAsync();
        Assert.AreEqual(meeting.Id, (await db.AccountRouteEvidence
            .SingleAsync(x => x.AccountId == account.Id)).SourceId);
        db.AccountRouteEvidence.Add(new AccountRouteEvidence {
            AccountId = account.Id, SourceKind = "MEETING", SourceId = meeting.Id,
            PolCode = "CNSHA", PoeCode = "PAMNZ", PodCode = "CRCAL",
            ObservedAt = DateTime.UtcNow, CreatedByUserId = user
        });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Company_profile_stores_details_without_modifying_existing_account()
    {
        await using var db = await Open();
        var user = Guid.NewGuid();
        var account = new Account { DisplayName = "Business", LegalName = "Business",
            CategoryCode = "D", ExecutiveUserId = user, CreatedByUserId = user };
        db.Accounts.Add(account);
        db.AccountBusinessProfiles.Add(new AccountBusinessProfile {
            AccountId = account.Id, Phone = "+506 2222 2222", Email = "ventas@example.com",
            Sector = "Automotriz", BusinessActivity = "Importación",
            Products = "Repuestos", CompanyHistory = "Empresa comercial",
            CommercialObservations = "Visita mensual",
            CommercialStrategy = "Crecimiento de importaciones",
            ProjectsOfInterest = "Apertura de mercado",
            CustomsInstructions = "Verificar permisos",
            CargoCollectionInstructions = "Confirmar bodega",
            WebCustomerReference = "CUST-WEB-88",
            OperationalInstructions = "Confirmar recepción", CreatedByUserId = user
        });
        await db.SaveChangesAsync();
        var found = await db.AccountBusinessProfiles.SingleAsync(x => x.AccountId == account.Id);
        Assert.AreEqual("Repuestos", found.Products);
        Assert.AreEqual("Crecimiento de importaciones", found.CommercialStrategy);
        Assert.AreEqual("Apertura de mercado", found.ProjectsOfInterest);
        Assert.AreEqual("Verificar permisos", found.CustomsInstructions);
        Assert.AreEqual("Confirmar bodega", found.CargoCollectionInstructions);
        Assert.AreEqual("CUST-WEB-88", found.WebCustomerReference);
        Assert.AreEqual("Confirmar recepción", found.OperationalInstructions);
        Assert.AreEqual("Business", (await db.Accounts.SingleAsync(x => x.Id == account.Id)).DisplayName);
    }

    [TestMethod]
    public async Task Real_postgres_rejects_opportunity_without_subject_and_negative_amount()
    {
        await using var db=await Open();
        var stage=await db.SalesPipelineStages.AsNoTracking().FirstAsync(x=>!x.IsWon&&!x.IsLost);
        var user=Guid.NewGuid();
        var invalid=new Opportunity {Title="Invalid",ExecutiveUserId=user,PipelineId=stage.PipelineId,StageId=stage.Id,
            AmountEstimated=10,CurrencyCode="USD",ProbabilityPct=20,CreatedByUserId=user};
        db.Opportunities.Add(invalid);
        var rejected=false;
        try{await db.SaveChangesAsync();}
        catch(DbUpdateException){rejected=true;}
        Assert.IsTrue(rejected,"A business subject is mandatory in the real schema");
    }
    [TestMethod]
    public async Task Commercial_entities_preserve_stages_and_multiple_routes()
    {
        await using var db=await Open();
        var user=Guid.NewGuid();
        var lead=new Lead {Name="Lead",ExecutiveUserId=user,CreatedByUserId=user};
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        var stage=await db.SalesPipelineStages.AsNoTracking().OrderBy(x=>x.SortOrder).FirstAsync();
        var o=new Opportunity {LeadId=lead.Id,Title="Ocean shipment",ExecutiveUserId=user,
            PipelineId=stage.PipelineId,StageId=stage.Id,AmountEstimated=9500m,
            CurrencyCode="USD",ProbabilityPct=stage.DefaultWinProbability,CreatedByUserId=user};
        db.Opportunities.Add(o);
        db.OpportunityStageHistories.Add(new OpportunityStageHistory {OpportunityId=o.Id,
            ToStageId=stage.Id,ChangedBy=user,CreatedByUserId=user});
        db.Activities.Add(new Activity {LeadId=lead.Id,OpportunityId=o.Id,OwnerUserId=user,
            Type="VISIT",Subject="Visit office",CreatedByUserId=user});
        var analysis=new CommercialAnalysis {LeadId=lead.Id,OpportunityId=o.Id,OwnerUserId=user,
            MarketCode="OTHER",EstimatedMonthlyVolume=25.75m,VolumeUnit="CBM",
            CustomsNationalizationRequired=true,CreatedByUserId=user};
        db.CommercialAnalyses.Add(analysis);
        db.CommercialAnalysisRoutes.Add(new CommercialAnalysisRoute {AnalysisId=analysis.Id,
            PolCode="CNSHA",PoeCode="CRCAL",FrequencyPerMonth=4,CreatedByUserId=user});
        db.CommercialAnalysisRoutes.Add(new CommercialAnalysisRoute {AnalysisId=analysis.Id,
            PolCode="CNNGB",PoeCode="CRCAL",FrequencyPerMonth=2,CreatedByUserId=user});
        db.CommercialAnalysisServices.Add(new CommercialAnalysisService {AnalysisId=analysis.Id,
            ServiceCode="CUSTOMS",ModalityCode="LCL",TransportModeCode="NONE",
            Volume=25.75m,VolumeUnit="CBM",RequiresCustoms=true,CreatedByUserId=user});
        await db.SaveChangesAsync();
        Assert.AreEqual(2,await db.CommercialAnalysisRoutes.CountAsync(x=>x.AnalysisId==analysis.Id));
        Assert.AreEqual(1,await db.OpportunityStageHistories.CountAsync(x=>x.OpportunityId==o.Id));
        Assert.AreEqual(1,await db.Activities.CountAsync(x=>x.OpportunityId==o.Id));
    }
    [TestMethod]
    public async Task Real_postgres_rejects_wrong_stage_pipeline_combination()
    {
        await using var db=await Open();
        var user=Guid.NewGuid();
        var account=new Account {DisplayName="Customer",LegalName="Customer",ExecutiveUserId=user,
            CategoryType="CUSTOMER",CategoryCode="D",CreatedByUserId=user};
        db.Accounts.Add(account);
        var p=new SalesPipeline {Name="Parallel "+Guid.NewGuid(),CreatedByUserId=user};
        db.SalesPipelines.Add(p);
        await db.SaveChangesAsync();
        var stage=await db.SalesPipelineStages.AsNoTracking().FirstAsync(x=>x.IsActive);
        var opportunity=new Opportunity {Title="Invalid stage",AccountId=account.Id,
            ExecutiveUserId=user,PipelineId=p.Id,StageId=stage.Id,AmountEstimated=1,
            CurrencyCode="USD",CreatedByUserId=user};
        db.Opportunities.Add(opportunity);
        var rejected=false;
        try{await db.SaveChangesAsync();}
        catch(DbUpdateException){rejected=true;}
        Assert.IsTrue(rejected,"SQL must reject a stage from another pipeline");
    }
}
