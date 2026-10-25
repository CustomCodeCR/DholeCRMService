using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmSchemaIntegrationTests
{
    [TestMethod]
    public async Task Versioned_migration_creates_the_ten_phase2_relations()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) Assert.Inconclusive("Real PostgreSQL required.");
        var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options;
        await using var db = new CrmDbContext(options);
        await db.Database.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema = 'crm'").ToListAsync();
        foreach (var name in new[] {
            "crm_accounts", "crm_leads", "crm_lead_conversions",
            "crm_account_assignments", "crm_contacts", "crm_account_contacts",
            "crm_addresses", "crm_contact_preferences", "crm_marketing_consents",
            "crm_account_business_activities"
        })
            CollectionAssert.Contains(tables, name, "Missing table " + name);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        CollectionAssert.Contains(applied.ToList(), "20261014193000_InitialCrmData");
    }

    [TestMethod]
    public async Task Duplicate_fiscal_identity_is_rejected_by_real_database()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) Assert.Inconclusive("Real PostgreSQL required.");
        var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options;
        await using var db = new CrmDbContext(options);
        await db.Database.MigrateAsync();
        var country = Guid.NewGuid();
        var user = Guid.NewGuid();
        Account Make() => new() { CountryId = country, TaxIdTypeCode = "CEDULA",
            TaxId = "001-2345", NormalizedTaxId = "0012345",
            DisplayName = "Prueba", LegalName = "Prueba", ExecutiveUserId = user,
            CategoryType = "CUSTOMER", CategoryCode = "D", CreatedByUserId = user };
        db.Accounts.Add(Make());
        await db.SaveChangesAsync();
        db.Accounts.Add(Make());
        var duplicateRejected = false;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { duplicateRejected = true; }
        Assert.IsTrue(duplicateRejected, "The database must enforce fiscal identity uniqueness.");
    }

    [TestMethod]
    public async Task Lead_conversion_has_unique_lead_constraint()
    {
        var cs = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) Assert.Inconclusive("Real PostgreSQL required.");
        var options = new DbContextOptionsBuilder<CrmDbContext>().UseNpgsql(cs).Options;
        await using var db = new CrmDbContext(options);
        await db.Database.MigrateAsync();
        var user = Guid.NewGuid();
        var lead = new Lead { Name = "Conversion test", ExecutiveUserId = user, CreatedByUserId = user };
        var account = new Account { DisplayName = "Converted", LegalName = "Converted",
            ExecutiveUserId = user, CategoryType = "CUSTOMER", CategoryCode = "D", CreatedByUserId = user };
        db.Leads.Add(lead);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.LeadConversions.Add(new LeadConversion { LeadId = lead.Id, AccountId = account.Id,
            ConvertedBy = user, CreatedByUserId = user });
        await db.SaveChangesAsync();
        db.LeadConversions.Add(new LeadConversion { LeadId = lead.Id, AccountId = account.Id,
            ConvertedBy = user, CreatedByUserId = user });
        var rejected = false;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { rejected = true; }
        Assert.IsTrue(rejected, "Multiple conversions of a lead must be rejected.");
    }
}
