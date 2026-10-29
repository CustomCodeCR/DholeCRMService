using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmDynamicsProfileTests
{
    [TestMethod]
    public void Dynamics_commercial_fields_are_mapped_with_explicit_limits()
    {
        var options = new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql("Host=localhost;Database=crm_schema_only;Username=unused;Password=unused")
            .Options;
        using var db = new CrmDbContext(options);
        var model = db.Model.FindEntityType(typeof(AccountBusinessProfile));
        Assert.IsNotNull(model);
        foreach (var (name, length) in new (string, int)[] {
            (nameof(AccountBusinessProfile.ActiveServices), 2000),
            (nameof(AccountBusinessProfile.PresenceCountries), 2000),
            (nameof(AccountBusinessProfile.CommercialStrategy), 4000),
            (nameof(AccountBusinessProfile.ProjectsOfInterest), 4000),
            (nameof(AccountBusinessProfile.PricingObservations), 4000),
            (nameof(AccountBusinessProfile.TariffMonitoringNotes), 4000),
            (nameof(AccountBusinessProfile.TariffSendingNotes), 4000),
            (nameof(AccountBusinessProfile.CustomsInstructions), 4000),
            (nameof(AccountBusinessProfile.CargoCollectionInstructions), 4000),
            (nameof(AccountBusinessProfile.DocumentReviewInstructions), 4000),
            (nameof(AccountBusinessProfile.AssignedOperations), 500),
            (nameof(AccountBusinessProfile.CompetitorObservations), 4000),
            (nameof(AccountBusinessProfile.TradeAgreementNotes), 2000),
            (nameof(AccountBusinessProfile.WebCustomerReference), 200),
        })
        {
            Assert.AreEqual(length, model.FindProperty(name)?.GetMaxLength(),
                "Invalid length for " + name);
        }
    }
}
