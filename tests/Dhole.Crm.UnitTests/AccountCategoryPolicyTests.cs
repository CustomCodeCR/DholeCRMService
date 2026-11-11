using Dhole.Crm.Domain;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class AccountCategoryPolicyTests
{
    [TestMethod]
    [DataRow("CUSTOMER", "A", "D")]
    [DataRow("CUSTOMER", "B", "D")]
    [DataRow("NVOCC", "A", "B")]
    [DataRow("LITTLE", "A", "A")]
    public void Unprivileged_creation_ignores_client_supplied_category(
        string kind, string submitted, string expected) =>
        Assert.AreEqual(expected, AccountCategoryPolicy.ResolveOnCreate(kind, submitted, false));

    [TestMethod]
    public void Explicit_scope_allows_valid_category_assignment()
    {
        Assert.AreEqual("B", AccountCategoryPolicy.ResolveOnCreate("CUSTOMER", "B", true));
        Assert.IsTrue(AccountCategoryPolicy.Valid("CUSTOMER", "B"));
        Assert.IsFalse(AccountCategoryPolicy.Valid("CUSTOMER", ""));
        Assert.IsFalse(AccountCategoryPolicy.Valid("NVOCC", "D"));
        Assert.IsFalse(AccountCategoryPolicy.Valid("WRONG", "A"));
    }

    [TestMethod]
    public void Update_without_scope_cannot_change_category_or_account_kind()
    {
        Assert.IsTrue(AccountCategoryPolicy.CanUpdate("CUSTOMER", "D", "CUSTOMER", "D", false));
        Assert.IsFalse(AccountCategoryPolicy.CanUpdate("CUSTOMER", "D", "CUSTOMER", "A", false));
        Assert.IsFalse(AccountCategoryPolicy.CanUpdate("CUSTOMER", "D", "NVOCC", "B", false));
        Assert.IsTrue(AccountCategoryPolicy.CanUpdate("CUSTOMER", "D", "CUSTOMER", "A", true));
    }
}
