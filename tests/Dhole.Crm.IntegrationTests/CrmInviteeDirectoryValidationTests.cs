using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Infrastructure.References;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class CrmInviteeDirectoryValidationTests
{
    [TestMethod]
    public async Task Two_valid_internal_users_are_accepted_without_individual_auth_lookups()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var auth = new FakeAuth([first, second]);
        Assert.IsTrue(await CrmInviteeDirectoryValidation.AllEligibleAsync(
            auth, [first, second], CancellationToken.None));
        Assert.AreEqual(0, auth.IndividualLookups);
        Assert.AreEqual(1, auth.DirectoryLookups);
    }

    [TestMethod]
    public async Task Guest_not_in_active_authorized_directory_is_rejected()
    {
        var valid = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var auth = new FakeAuth([valid]);
        Assert.IsFalse(await CrmInviteeDirectoryValidation.AllEligibleAsync(
            auth, [valid, inactive], CancellationToken.None));
        Assert.AreEqual(0, auth.IndividualLookups);
    }

    [TestMethod]
    public async Task Invited_user_can_be_found_on_another_page()
    {
        var users = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToArray();
        var auth = new FakeAuth(users);
        Assert.IsTrue(await CrmInviteeDirectoryValidation.AllEligibleAsync(
            auth, [users[119], users[0]], CancellationToken.None));
        Assert.AreEqual(2, auth.DirectoryLookups);
    }

    [TestMethod]
    public async Task Empty_invitation_does_not_call_auth()
    {
        var auth = new FakeAuth([]);
        Assert.IsTrue(await CrmInviteeDirectoryValidation.AllEligibleAsync(
            auth, Array.Empty<Guid>(), CancellationToken.None));
        Assert.AreEqual(0, auth.DirectoryLookups);
    }

    private sealed class FakeAuth(Guid[] activeAgendaUsers) : IAuthReferenceService
    {
        public int IndividualLookups { get; private set; }
        public int DirectoryLookups { get; private set; }

        public Task<AuthUserReference?> GetActiveAsync(Guid userId, CancellationToken ct)
        {
            IndividualLookups++;
            throw new InvalidOperationException("The invitation flow must not invoke per-user Auth permission evaluation");
        }

        public Task<CrmUserDirectoryPage> GetDirectoryAsync(
            int page, int pageSize, string? search, CancellationToken ct)
        {
            DirectoryLookups++;
            var slice = activeAgendaUsers.Skip((page-1)*pageSize).Take(pageSize)
                .Select(id => new CrmUserDirectoryItem(id, "Invitado", "invited-user")).ToArray();
            return Task.FromResult(new CrmUserDirectoryPage(activeAgendaUsers.Length,page,pageSize,slice));
        }
    }
}
