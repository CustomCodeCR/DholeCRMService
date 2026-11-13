using System.Reflection;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmBitacoraParentLinkTests
{
    [TestMethod]
    public void Followup_model_tracks_origin_activity_with_restricted_foreign_key()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql("Host=localhost;Database=crm_parent_model_test;Username=test;Password=test")
            .Options);
        var entity = db.Model.FindEntityType(typeof(Followup));
        Assert.IsNotNull(entity);
        var origin = entity.FindProperty(nameof(Followup.OriginActivityId));
        Assert.IsNotNull(origin);
        Assert.IsTrue(entity.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Contains(origin) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(Activity)));
    }

    [TestMethod]
    public void Historic_bitacora_reason_still_resolves_its_parent_guid()
    {
        var endpoint = typeof(Dhole.Crm.Api.Endpoints.ActivityCreateRequest).Assembly
            .GetType("Dhole.Crm.Api.Endpoints.CrmFollowupEndpoints");
        Assert.IsNotNull(endpoint);
        var parser = endpoint.GetMethod("TryActivityOrigin",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(parser);
        var activity = Guid.NewGuid();
        foreach(var reason in new[]{
            "Acuerdo registrado en actividad comercial " + activity,
            "Acuerdo registrado en actividad comercial " + activity + " (tarea de bitácora)"
        })
        {
            object?[] args = [reason, Guid.Empty];
            Assert.IsTrue((bool)parser.Invoke(null, args)!);
            Assert.AreEqual(activity, args[1]);
        }
        object?[] invalid = ["Recordatorio manual", Guid.Empty];
        Assert.IsFalse((bool)parser.Invoke(null, invalid)!);
    }
}
