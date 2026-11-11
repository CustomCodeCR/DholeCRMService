using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>
/// Scoped batch labels for calendar rows. Only names of accessible clients,
/// prospects and responsible users already linked to those records are returned.
/// Ordinary sellers cannot query an unrestricted Auth employee directory.
/// </summary>
internal static class CrmCalendarLabelsEndpoints
{
    public static RouteGroupBuilder MapCalendarLabels(this RouteGroupBuilder group)
    {
        group.MapGet("/calendar/labels", Get);
        group.MapGet("/calendar/users", GetUserDirectory);
        return group;
    }

    private static async Task<IResult> GetUserDirectory(int? page,int? pageSize,string? search,
        HttpContext http,IAuthReferenceService auth,CancellationToken ct)
    {
        if(!CrmAccess.IsCalendarAdministrator(http) && !CrmAccess.CanManageAgendaAcrossUsers(http))return CrmAccess.Denied();
        if(search?.Length>100)return Results.BadRequest(new{error="Search too long"});
        var n=Math.Max(1,page??1);
        var size=Math.Clamp(pageSize??100,1,100);
        var result=await auth.GetDirectoryAsync(n,size,search,ct);
        return Results.Ok(result);
    }

    private static Guid[] Parse(string? values)
    {
        var split=(values??"").Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(split.Length>100 || split.Any(s=>!Guid.TryParse(s,out _)))
            throw new ArgumentException("At most 100 valid IDs are accepted");
        return split.Select(Guid.Parse).Distinct().ToArray();
    }

    private static async Task<IResult> Get(string? accountIds,string? leadIds,string? ownerIds,
        CrmDbContext db,HttpContext http,IAuthReferenceService auth,IMemoryCache cache,
        CancellationToken ct)
    {
        if(!CrmDataEndpoints.HasRead(http,"activity") &&
           !CrmAccess.Scope(http,"crm.task.manage") &&
           !CrmAccess.Scope(http,"crm.followup.manage") &&
           !CrmAccess.Scope(http,"crm.activity.read-all") &&
           !CrmAccess.IsCalendarAdministrator(http) &&
           !CrmAccess.CanManageAgendaAcrossUsers(http))
            return CrmAccess.Denied();
        Guid[] wantedAccounts,wantedLeads,wantedOwners;
        try
        {
            wantedAccounts=Parse(accountIds);
            wantedLeads=Parse(leadIds);
            wantedOwners=Parse(ownerIds);
        }
        catch(ArgumentException){return Results.BadRequest(new{error="Invalid calendar label IDs"});}

        var accounts=await CrmAccess.CalendarVisibleAccounts(db,http).AsNoTracking()
            .Where(a=>wantedAccounts.Contains(a.Id))
            .Select(a=>new{a.Id,a.DisplayName,a.LegalName})
            .ToListAsync(ct);
        var actor=CrmAccess.UserId(http);
        var leadAccess=CrmAccess.Scope(http,"crm.lead.read-all") || CrmAccess.IsCalendarAdministrator(http);
        var leads=await db.Leads.AsNoTracking()
            .Where(l=>wantedLeads.Contains(l.Id)&&l.ArchivedAt==null &&
                (leadAccess||l.ExecutiveUserId==actor))
            .Select(l=>new{l.Id,l.Name,l.CompanyName})
            .ToListAsync(ct);
        var validAccounts=accounts.Select(a=>a.Id).ToArray();
        var validLeads=leads.Select(l=>l.Id).ToArray();
        // Resolve only owners who have an event linked to a visible client/lead.
        var allActivities=CrmAccess.CanManageAgendaAcrossUsers(http) || CrmAccess.IsCalendarAdministrator(http);
        var allAccounts=CrmAccess.Scope(http,"crm.account.read-all") || CrmAccess.IsCalendarAdministrator(http);
        var allLeads=CrmAccess.Scope(http,"crm.lead.read-all") || CrmAccess.IsCalendarAdministrator(http);
        var ownersFromActivities=await db.Activities.AsNoTracking()
            .Where(a=>a.ArchivedAt==null && wantedOwners.Contains(a.OwnerUserId) &&
                ((a.AccountId.HasValue&&validAccounts.Contains(a.AccountId.Value))||
                 (a.LeadId.HasValue&&validLeads.Contains(a.LeadId.Value))) &&
                (a.OwnerUserId==actor||allActivities))
            .Select(a=>a.OwnerUserId).Distinct().ToListAsync(ct);
        var ownersFromFollowups=await db.Followups.AsNoTracking()
            .Where(f=>f.ArchivedAt==null && wantedOwners.Contains(f.AssignedUserId) &&
                ((f.AccountId.HasValue&&validAccounts.Contains(f.AccountId.Value))||
                 (f.LeadId.HasValue&&validLeads.Contains(f.LeadId.Value))) &&
                (f.AssignedUserId==actor||allAccounts||allLeads))
            .Select(f=>f.AssignedUserId).Distinct().ToListAsync(ct);
        var permitted=ownersFromActivities.Concat(ownersFromFollowups)
            .Append(actor).Distinct().ToArray();
        using var gate=new SemaphoreSlim(4);
        var ownerNames=await Task.WhenAll(permitted.Select(async id=>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result=await cache.GetOrCreateAsync("crm:calendar:name:"+id.ToString("N"),async entry=>
                {
                    entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(2);
                    var user=await auth.GetActiveAsync(id,ct);
                    return user?.DisplayName??"Vendedor no disponible";
                });
                return (Id:id,Name:result??"Vendedor no disponible");
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception){return (Id:id,Name:"Vendedor no disponible");}
            finally{gate.Release();}
        }));
        return Results.Ok(new {
            accounts=accounts.ToDictionary(a=>a.Id.ToString(),a=>
                string.IsNullOrWhiteSpace(a.DisplayName)?a.LegalName:a.DisplayName),
            leads=leads.ToDictionary(l=>l.Id.ToString(),l=>
                string.IsNullOrWhiteSpace(l.CompanyName)?l.Name:l.CompanyName),
            owners=ownerNames.ToDictionary(x=>x.Id.ToString(),x=>x.Name)
        });
    }
}
