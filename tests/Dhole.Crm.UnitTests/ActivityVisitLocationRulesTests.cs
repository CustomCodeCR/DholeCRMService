using Dhole.Crm.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Dhole.Crm.UnitTests;
[TestClass]
public sealed class ActivityVisitLocationRulesTests
{
    [TestMethod]
    public void VisitCoordinateValidation()
    {
        Assert.IsTrue(ActivityVisitLocationRules.IsValid("VISIT", 9.928069m, -84.090725m));
        Assert.IsTrue(ActivityVisitLocationRules.IsValid("VISIT", null, null));
        Assert.IsTrue(ActivityVisitLocationRules.IsValid("CALL", null, null));
        Assert.IsFalse(ActivityVisitLocationRules.IsValid("CALL", 9m, -84m));
        Assert.IsFalse(ActivityVisitLocationRules.IsValid("VISIT", 9m, null));
        Assert.IsFalse(ActivityVisitLocationRules.IsValid("VISIT", 91m, -84m));
        Assert.IsFalse(ActivityVisitLocationRules.IsValid("VISIT", 9m, -181m));
    }
}
