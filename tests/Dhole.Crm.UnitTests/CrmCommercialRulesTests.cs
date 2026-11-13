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
    public void Historical_activities_can_be_registered_without_retroactive_reminders()
    {
        var now=new DateTime(2026,11,10,20,0,0,DateTimeKind.Utc);
        var start=now.AddDays(-5);
        var end=start.AddHours(1);
        Assert.IsTrue(CommercialRules.ValidActivitySchedule(start,end));
        Assert.IsTrue(CommercialRules.IsHistoricalActivity(end,now));
        Assert.IsFalse(CommercialRules.IsHistoricalActivity(now.AddHours(1),now));
        Assert.IsFalse(CommercialRules.IsHistoricalActivity(null,now));
        Assert.IsFalse(CommercialRules.ValidActivitySchedule(end,start));
    }
    [TestMethod]
    public void Unscheduled_actions_are_completed_without_reminder_scheduling()
    {
        var now=new DateTime(2026,11,12,21,0,0,DateTimeKind.Utc);
        Assert.IsTrue(CommercialRules.ValidActivitySchedule(null,null));
        Assert.AreEqual(now,CommercialRules.ResolveOccurredAt(null,null,null,now));
        Assert.IsNull(CommercialRules.ResolveOccurredAt(now.AddHours(1),
            now.AddHours(2),null,now));
        var start=now.AddDays(-1);
        var end=start.AddHours(1);
        Assert.AreEqual(end,CommercialRules.ResolveOccurredAt(start,end,null,now));
        Assert.AreEqual(start,CommercialRules.ResolveOccurredAt(null,null,start,now));
    }

    [TestMethod]
    public void CommercialLog_IncludesCompletedActions_NotScheduledOrCancelled()
    {
        Assert.IsTrue(CommercialRules.IsCommercialLog("Completed"));
        Assert.IsFalse(CommercialRules.IsCommercialLog("Scheduled"));
        Assert.IsFalse(CommercialRules.IsCommercialLog("Pending"));
        Assert.IsFalse(CommercialRules.IsCommercialLog("Cancelled"));
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
    [TestMethod]
    public void Commercial_activity_types_allow_extended_sales_actions_without_changing_followup_semantics()
    {
        foreach (var activityType in new[]
        {
            "VISIT", "CALL", "EMAIL", "MEETING", "OTHER",
            "COMMERCIAL_FOLLOWUP", "WHATSAPP", "RECOMMENDATION", "OFFER"
        })
            Assert.IsTrue(CommercialRules.ValidActivityType(activityType), activityType);

        // FOLLOWUP is a separate reminder/commitment resource, not an activity type.
        Assert.IsFalse(CommercialRules.ValidActivityType("FOLLOWUP"));
        Assert.IsFalse(CommercialRules.ValidActivityType("UNKNOWN"));
        Assert.IsFalse(CommercialRules.ValidActivityType(null));
    }

}
