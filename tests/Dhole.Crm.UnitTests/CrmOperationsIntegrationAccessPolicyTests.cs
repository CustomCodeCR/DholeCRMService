using Dhole.Crm.Domain;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmOperationsIntegrationAccessPolicyTests
{
    [TestMethod]
    public void ServiceKeyNeedsBothNonEmptyMatchingSecrets()
    {
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.HasServiceKey(null, "key"));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.HasServiceKey("key", null));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.HasServiceKey("secret", "Secret"));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.HasServiceKey("secret", "secreT"));
        Assert.IsTrue(CrmOperationsIntegrationAccessPolicy.HasServiceKey(
            "long-explicit-internal-service-key", "long-explicit-internal-service-key"));
    }

    [TestMethod]
    public void InternalReaderRequiresActiveAuthIdentityAndCrmReadAll()
    {
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.IsAuthorized(false, ["crm.account.read-all"]));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.IsAuthorized(true, ["crm.account.read"]));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.IsAuthorized(true, []));
        Assert.IsFalse(CrmOperationsIntegrationAccessPolicy.IsAuthorized(true, null));
        Assert.IsTrue(CrmOperationsIntegrationAccessPolicy.IsAuthorized(
            true, ["operations.case.create", "CRM.ACCOUNT.READ-ALL"]));
    }
}
