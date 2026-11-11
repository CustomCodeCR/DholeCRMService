using Dhole.Crm.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Dhole.Crm.UnitTests;
[TestClass]
public sealed class LeadLocationRulesTests
{
    [TestMethod]
    public void ValidatesStoredProspectAddressesAndCoordinates()
    {
        Assert.IsTrue(LeadLocationRules.IsValid("San José, Costa Rica","WORK",null,null));
        Assert.IsTrue(LeadLocationRules.IsValid("Aduanera Castro Fallas","WORK",9.93m,-84.08m));
        Assert.IsFalse(LeadLocationRules.IsValid("A","WORK",null,-84m));
        Assert.IsFalse(LeadLocationRules.IsValid("A","WORK",100m,-84m));
        Assert.IsFalse(LeadLocationRules.IsValid("A","WORK",9m,-190m));
        Assert.IsFalse(LeadLocationRules.IsValid("","WORK",null,null));
    }
}
