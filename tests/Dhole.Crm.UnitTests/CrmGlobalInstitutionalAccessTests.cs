using Dhole.Crm.Domain;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmGlobalInstitutionalAccessTests
{
    [TestMethod]
    public void Castro_fallas_viewer_grant_is_global_and_does_not_require_account_id()
    {
        var grant = new CastroFallasViewerGrant
        {
            UserId = Guid.NewGuid(),
            GrantedByUserId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid()
        };
        Assert.AreNotEqual(Guid.Empty, grant.UserId);
        Assert.IsNull(grant.RevokedAt);
        Assert.IsNull(typeof(CastroFallasViewerGrant).GetProperty("AccountId"),
            "Institutional access must be global, never per customer account.");
    }

    [TestMethod]
    public void Revoked_global_authorization_preserves_audit()
    {
        var grant = new CastroFallasViewerGrant
        {
            UserId = Guid.NewGuid(), GrantedByUserId = Guid.NewGuid(),
            CreatedByUserId = Guid.NewGuid(), RevokedAt = DateTime.UtcNow
        };
        Assert.IsTrue(grant.RevokedAt.HasValue);
        Assert.AreEqual(1L, grant.RowVersion);
    }
}
