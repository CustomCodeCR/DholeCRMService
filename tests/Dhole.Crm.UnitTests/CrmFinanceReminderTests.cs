using Dhole.Crm.Workers.Followups;
namespace Dhole.Crm.UnitTests;
[TestClass]
public sealed class CrmFinanceReminderTests
{
    [TestMethod]
    public void Renewal_thresholds_are_deduplicated_and_ordered()
    {
        CollectionAssert.AreEqual(new[]{60,30,15,7},
            CrmFinanceReminderEngine.ParseThresholds("7,60,30,15,7",[1]));
    }
    [TestMethod]
    public void Invalid_thresholds_fail_closed_to_safe_defaults()
    {
        CollectionAssert.AreEqual(new[]{30,7,0},
            CrmFinanceReminderEngine.ParseThresholds("-1,400",[30,7,0]));
    }
}
