using Dhole.Crm.Infrastructure.References;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Validate invitees using the same internal, active-user, effective CRM scope
/// directory used by the calendar selector. IDs not in that directory are rejected.
/// </summary>
public static class CrmInviteeDirectoryValidation
{
    public static async Task<bool> AllEligibleAsync(
        IAuthReferenceService auth, IReadOnlyCollection<Guid> inviteeIds,
        CancellationToken cancellationToken)
    {
        if(inviteeIds.Count == 0) return true;
        if(inviteeIds.Contains(Guid.Empty)) return false;
        var missing = inviteeIds.ToHashSet();
        const int pageSize = 100;
        // Do not trust client-provided labels or authorization claims.
        for(var page = 1; page <= 100; page++)
        {
            var directory = await auth.GetDirectoryAsync(page, pageSize, null, cancellationToken);
            foreach(var user in directory.Items) missing.Remove(user.Id);
            if(missing.Count == 0) return true;
            if(directory.Items.Length == 0 || page * pageSize >= directory.Total) return false;
        }
        return false;
    }
}
