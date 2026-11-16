using Dhole.Crm.Application.Commercial;

namespace Dhole.Crm.UnitTests;

[TestClass]
public class ActivityCompletionRulesTests
{
    [TestMethod]
    public void MissingTimestampIsAcceptedForServerClock()
    {
        var now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
        Assert.IsTrue(CommercialRules.ValidActivityCompletionTime(null, now));
    }

    [TestMethod]
    public void ExplicitPastUtcTimestampIsAccepted()
    {
        var now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
        Assert.IsTrue(CommercialRules.ValidActivityCompletionTime(now.AddHours(-1), now));
    }

    [TestMethod]
    public void FutureOrNonUtcTimestampIsRejected()
    {
        var now = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
        Assert.IsFalse(CommercialRules.ValidActivityCompletionTime(now.AddSeconds(1), now));
        Assert.IsFalse(CommercialRules.ValidActivityCompletionTime(DateTime.SpecifyKind(now, DateTimeKind.Local), now));
        Assert.IsFalse(CommercialRules.ValidActivityCompletionTime(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), now));
    }

    [TestMethod]
    public void LegacyStatusLabelMapsToCanonicalConfigCode()
    {
        Assert.AreEqual("COMPLETED", CommercialRules.NormalizeActivityOutcome("Completed"));
        Assert.AreEqual("COMPLETED", CommercialRules.NormalizeActivityOutcome(" completed "));
        Assert.AreEqual("SUCCESS", CommercialRules.NormalizeActivityOutcome(" SUCCESS "));
        Assert.AreEqual(string.Empty, CommercialRules.NormalizeActivityOutcome(null));
    }
}
