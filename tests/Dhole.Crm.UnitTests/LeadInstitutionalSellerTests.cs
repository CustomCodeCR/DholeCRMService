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
}
