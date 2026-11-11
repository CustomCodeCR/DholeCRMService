using Dhole.Crm.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class LeadDuplicateIdentityTests
{
    [TestMethod]
    public void MatchesTaxIdIgnoringSeparatorsAndCase()
    {
        var lead = new Lead { Name = "OTRA EMPRESA", TaxId = "3-101-123456" };
        Assert.IsTrue(LeadDuplicateIdentity.Matches(lead, "Nombre nuevo", null, null, "3101 123456"));
    }

    [TestMethod]
    public void MatchesCompanyNameAcrossSellersWithoutTaxId()
    {
        var lead = new Lead { Name = "COMPUTADORAS PCI", CompanyName = "PCI SOCIEDAD ANONIMA" };
        Assert.IsTrue(LeadDuplicateIdentity.Matches(lead, "Otro nombre", " pci  sociedad   anonima ", null, null));
    }

    [TestMethod]
    public void AllowsDifferentCountryAndArchivedLead()
    {
        var country = Guid.NewGuid();
        var lead = new Lead { Name = "TRANSPORTES", CountryId = country, TaxId = "1234" };
        Assert.IsFalse(LeadDuplicateIdentity.Matches(lead, "Transportes", null, Guid.NewGuid(), "1234"));
        lead.ArchivedAt = DateTime.UtcNow;
        Assert.IsFalse(LeadDuplicateIdentity.Matches(lead, "Transportes", null, country, "1234"));
    }

    [TestMethod]
    public void DeterministicLocksForEquivalentIdentity()
    {
        var country = Guid.NewGuid();
        CollectionAssert.AreEqual(
            LeadDuplicateIdentity.LockKeys("ACME", "ACME SA", country, "3-101-123456"),
            LeadDuplicateIdentity.LockKeys(" acme ", " acme   sa ", country, "3101123456"));
    }
}
