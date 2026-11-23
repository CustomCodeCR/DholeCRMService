using Dhole.Crm.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class LeadChangeHistoryTests
{
    [TestMethod]
    public void RecordsLifecycleAndLocationEvents()
    {
        Assert.AreEqual("CREATED", LeadChangeAudit.Action(true, false, new[] { "Name" }));
        Assert.AreEqual("UPDATED", LeadChangeAudit.Action(false, false, new[] { "Phone" }));
        Assert.AreEqual("ASSIGNED", LeadChangeAudit.Action(false, false, new[] { "ExecutiveUserId" }));
        Assert.AreEqual("ASSIGNED", LeadChangeAudit.Action(false, false, new[] { "IsCastroFallasExecutive" }));
        Assert.AreEqual("ARCHIVED", LeadChangeAudit.Action(false, false, new[] { "ArchivedAt" }));
        Assert.AreEqual("CONVERTED", LeadChangeAudit.Action(false, false, new[] { "ConvertedAt" }));
        Assert.AreEqual("LOCATION_CREATED", LeadChangeAudit.Action(true, true, new[] { "AddressText" }));
        Assert.AreEqual("LOCATION_UPDATED", LeadChangeAudit.Action(false, true, new[] { "AddressText" }));
    }
}
