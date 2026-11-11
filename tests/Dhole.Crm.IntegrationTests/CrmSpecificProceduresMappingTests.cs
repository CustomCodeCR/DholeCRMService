using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmSpecificProceduresMappingTests
{
    [TestMethod]
    public void Procedures_are_one_to_many_and_scoped_to_account()
    {
        var options = new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql("Host=localhost;Database=dhole_model_metadata_only;Username=postgres;Password=unused")
            .Options;
        using var db = new CrmDbContext(options);
        var type = db.Model.FindEntityType(typeof(AccountSpecificProcedure));
        Assert.IsNotNull(type);
        Assert.AreEqual("crm_account_specific_procedures", type.GetTableName());
        var accountKey = type.FindProperty(nameof(AccountSpecificProcedure.AccountId));
        Assert.IsNotNull(accountKey);
        Assert.IsFalse(type.GetIndexes().Any(index =>
            index.IsUnique && index.Properties.Contains(accountKey)),
            "Multiple procedures must be allowed for the same customer");
        Assert.AreEqual(160, type.FindProperty(nameof(AccountSpecificProcedure.Name))?.GetMaxLength());
        Assert.AreEqual(8000, type.FindProperty(nameof(AccountSpecificProcedure.Detail))?.GetMaxLength());
        Assert.AreEqual(500, type.FindProperty(nameof(AccountSpecificProcedure.AssignedTeam))?.GetMaxLength());
    }
}
