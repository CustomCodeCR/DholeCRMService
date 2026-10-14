using Dhole.Crm.Application.Commercial;

namespace Dhole.Crm.UnitTests;
[TestClass]
public sealed class CrmCommercialRulesTests
{
    [TestMethod]
    public void Opportunity_must_have_exactly_one_subject_and_valid_amount()
    {
        var id=Guid.NewGuid();
        Assert.IsTrue(CommercialRules.ValidOpportunitySubject(id,null));
        Assert.IsTrue(CommercialRules.ValidOpportunitySubject(null,id));
        Assert.IsFalse(CommercialRules.ValidOpportunitySubject(null,null));
        Assert.IsFalse(CommercialRules.ValidOpportunitySubject(id,id));
        Assert.IsFalse(CommercialRules.ValidMoney(-1,50));
        Assert.IsFalse(CommercialRules.ValidMoney(10,101));
        Assert.IsTrue(CommercialRules.ValidMoney(100.23m,25.5m));
    }
    [TestMethod]
    public void Closing_a_pipeline_requires_a_terminal_state_and_loss_reason()
    {
        Assert.IsTrue(CommercialRules.ValidStage(100,true,false));
        Assert.IsTrue(CommercialRules.ValidStage(0,false,true));
        Assert.IsFalse(CommercialRules.ValidStage(10,true,false));
        Assert.IsFalse(CommercialRules.ValidStage(50,true,true));
        Assert.IsTrue(CommercialRules.ValidClose(true,false,null));
        Assert.IsFalse(CommercialRules.ValidClose(false,true,null));
        Assert.IsTrue(CommercialRules.ValidClose(false,true,"PRICE"));
    }
    [TestMethod]
    public void Activity_schedule_must_be_UTC_and_ordered()
    {
        var start=new DateTime(2026,10,14,20,0,0,DateTimeKind.Utc);
        Assert.IsTrue(CommercialRules.ValidActivitySchedule(start,start.AddHours(1)));
        Assert.IsFalse(CommercialRules.ValidActivitySchedule(start,start.AddMinutes(-1)));
        Assert.IsFalse(CommercialRules.ValidActivitySchedule(start.ToLocalTime(),start.AddHours(1)));
        Assert.IsTrue(CommercialRules.ValidActivityType("VISIT"));
        Assert.IsFalse(CommercialRules.ValidActivityType("UNKNOWN"));
        Assert.IsFalse(CommercialRules.ValidVolume(-0.01m,"CBM"));
        Assert.IsTrue(CommercialRules.ValidVolume(10.2500m,"CFT"));
    }
}
