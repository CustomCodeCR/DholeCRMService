using Dhole.Crm.Contracts.V1;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Dhole.Crm.Api.Grpc;
public sealed class CrmOpportunityReadGrpcService(CrmDbContext db,IAuthReferenceService auth,
    IConfiguration cfg) : CrmOpportunityReadService.CrmOpportunityReadServiceBase
{
    public override async Task<OpportunitySummaryReply> GetOpportunityById(GetOpportunityByIdRequest r,ServerCallContext c)
    {
        var actor=await CrmGrpcAuthorization.RequireActorAsync(c,cfg,auth);
        CrmGrpcAuthorization.Need(actor.Scopes.Contains("crm.opportunity.read")||
                                  actor.Scopes.Contains("crm.opportunity.read-all"));
        var id=CrmGrpcAuthorization.RequiredGuid(r.OpportunityId);
        var item=await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,c.CancellationToken);
        if(item is null)return new OpportunitySummaryReply {Found=false};
        CrmGrpcAuthorization.Need(item.ExecutiveUserId==actor.UserId||
                                  actor.Scopes.Contains("crm.opportunity.read-all"));
        return Summary(item);
    }
    public override async Task<OpportunitySummariesReply> GetOpportunitiesByAccount(
        GetOpportunitiesByAccountRequest r,ServerCallContext c)
    {
        var actor=await CrmGrpcAuthorization.RequireActorAsync(c,cfg,auth);
        CrmGrpcAuthorization.Need((actor.Scopes.Contains("crm.opportunity.read")||
                                   actor.Scopes.Contains("crm.opportunity.read-all"))&&
                                  (actor.Scopes.Contains("crm.account.read")||
                                   actor.Scopes.Contains("crm.account.read-all")));
        var id=CrmGrpcAuthorization.RequiredGuid(r.AccountId);
        var account=await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,c.CancellationToken);
        if(account is null)return new OpportunitySummariesReply();
        CrmGrpcAuthorization.Need(account.ExecutiveUserId==actor.UserId||
                                  actor.Scopes.Contains("crm.account.read-all"));
        var offset=0;
        if(!string.IsNullOrWhiteSpace(r.PageToken) &&
           (!int.TryParse(r.PageToken,NumberStyles.None,CultureInfo.InvariantCulture,out offset)||offset<0))
            throw new RpcException(new Status(StatusCode.InvalidArgument,"Invalid page token"));
        var size=Math.Clamp(r.PageSize<=0?25:r.PageSize,1,100);
        var query=db.Opportunities.AsNoTracking().Where(x=>x.AccountId==id&&x.ArchivedAt==null);
        if(!actor.Scopes.Contains("crm.opportunity.read-all"))
            query=query.Where(x=>x.ExecutiveUserId==actor.UserId);
        var items=await query.OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip(offset).Take(size+1).ToListAsync(c.CancellationToken);
        var reply=new OpportunitySummariesReply {
            NextPageToken=items.Count>size?(offset+size).ToString(CultureInfo.InvariantCulture):""
        };
        reply.Opportunities.AddRange(items.Take(size).Select(Summary));
        return reply;
    }
    private static OpportunitySummaryReply Summary(Opportunity x)=>new(){
        Found=true,OpportunityId=x.Id.ToString(),AccountId=x.AccountId?.ToString()??"",
        LeadId=x.LeadId?.ToString()??"",Title=x.Title,ExecutiveUserId=x.ExecutiveUserId.ToString(),
        StageId=x.StageId.ToString(),CommercialStatus=x.Status,CurrencyCode=x.CurrencyCode,
        EstimatedAmount=x.AmountEstimated.ToString(CultureInfo.InvariantCulture)
    };
}
