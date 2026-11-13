using Dhole.Crm.Workers.Followups;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmReminderMessageContentTests
{
    [TestMethod]
    public void Activity_email_includes_customer_and_direct_clickable_link()
    {
        var id=Guid.Parse("7a648c89-6d4a-48ec-a81b-a7c764c61dc9");
        var route=CrmReminderMessageContent.RouteFor("Activity",id)!;
        var html=CrmReminderMessageContent.Render("Recordatorio: visita comercial",
            "Email","Cliente","Compañía Castro & Hijos",route,"https://dhole.example.test/");
        Assert.IsTrue(html.Contains("<strong>Cliente:</strong> " + System.Net.WebUtility.HtmlEncode("Compañía Castro & Hijos"),StringComparison.Ordinal));
        Assert.IsTrue(html.Contains($"href=\"https://dhole.example.test/crm/activities/{id:D}\"",StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("Ver seguimiento en Dhole",StringComparison.Ordinal));
    }

    [TestMethod]
    public void Prospect_email_encodes_untrusted_name_and_links_to_followup()
    {
        var id=Guid.NewGuid();
        var route=CrmReminderMessageContent.RouteFor("Followup",id)!;
        var html=CrmReminderMessageContent.Render("<img src=x onerror=alert(1)>",
            "Email","Prospecto","A < B & C",route,"https://staging.example.test");
        Assert.IsTrue(html.Contains("<strong>Prospecto:</strong> A &lt; B &amp; C",StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<img ",StringComparison.Ordinal));
        Assert.IsTrue(html.Contains($"href=\"https://staging.example.test/crm/followups/{id:D}\"",StringComparison.Ordinal));
    }

    [TestMethod]
    public void System_notification_stays_plain_text_and_unknown_urls_are_blocked()
    {
        var id=Guid.NewGuid();
        var route=CrmReminderMessageContent.RouteFor("Activity",id)!;
        var text=CrmReminderMessageContent.Render("Recordatorio","System",
            "Cliente","Acme",route,"https://dhole.example.test");
        Assert.AreEqual("Recordatorio\nCliente: Acme",text);
        Assert.IsNull(CrmReminderMessageContent.AbsoluteLink(route,"http://unsafe.example.test"));
        Assert.IsNull(CrmReminderMessageContent.AbsoluteLink("//evil.example.test","https://dhole.example.test"));
        Assert.IsNull(CrmReminderMessageContent.RouteFor("Lead",id));
    }
}
