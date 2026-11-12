using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class LeadInstitutionalSellerTests
{
    [TestMethod]
    public void OrdinaryProspectsRemainOwnedByAnAuthExecutive()
    {
        var lead = new Lead { ExecutiveUserId = Guid.NewGuid() };
        Assert.IsFalse(lead.IsCastroFallasExecutive);
    }

    [TestMethod]
    public void InstitutionalProspectRetainsAccountableAuthOwner()
    {
        var owner = Guid.NewGuid();
        var lead = new Lead { ExecutiveUserId = owner, IsCastroFallasExecutive = true };
        Assert.AreEqual(owner, lead.ExecutiveUserId);
        Assert.IsTrue(lead.IsCastroFallasExecutive);
    }

    [TestMethod]
    public void EfModelPersistsTheInstitutionalSellerFlag()
    {
        var options = new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql("Host=localhost;Database=dhole_crm;Username=unused;Password=unused")
            .Options;
        using var db = new CrmDbContext(options);
        var entity = db.Model.FindEntityType(typeof(Lead));
        Assert.IsNotNull(entity);
        var property = entity.FindProperty(nameof(Lead.IsCastroFallasExecutive));
        Assert.IsNotNull(property);
        Assert.AreEqual("is_castro_fallas_executive", property.GetColumnName());
    }
    [TestMethod]
    public void PricingClientPickerProjectsTheInstitutionalLeadFlag()
    {
        // Prevent the regression where the CRM prospect view displayed Castro Fallas
        // but pricing-client-options hard-coded false and displayed the Auth creator.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "src", "Dhole.Crm.Api", "Endpoints", "PricingClientOptionsEndpoints.cs")))
            root = root.Parent;
        Assert.IsNotNull(root, "Pricing endpoint source must exist in the checked-out repository");
        var endpoint = File.ReadAllText(Path.Combine(root.FullName,
            "src", "Dhole.Crm.Api", "Endpoints", "PricingClientOptionsEndpoints.cs"));
        StringAssert.Contains(endpoint, "IsCastroFallasExecutive = x.IsCastroFallasExecutive");
        Assert.DoesNotContain("IsCastroFallasExecutive = false", endpoint,
            "Pricing must not discard the institutional-seller flag when projecting leads");
        StringAssert.Contains(endpoint, "x.IsCastroFallasExecutive ? \"Castro Fallas\"");
    }

}
