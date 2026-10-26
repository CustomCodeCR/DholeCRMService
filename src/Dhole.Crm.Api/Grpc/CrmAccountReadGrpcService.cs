using Dhole.Crm.Contracts.V1;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Api.Grpc;
public sealed class CrmAccountReadGrpcService(CrmDbContext db,IAuthReferenceService auth,
    IConfiguration cfg) : CrmAccountReadService.CrmAccountReadServiceBase
{
    public override async Task<AccountSummaryReply> GetAccountById(GetAccountByIdRequest r,ServerCallContext c)
    {
        var actor=await CrmGrpcAuthorization.RequireActorAsync(c,cfg,auth);
        CrmGrpcAuthorization.Need(actor.Scopes.Contains("crm.account.read")||
                                  actor.Scopes.Contains("crm.account.read-all"));
        var id=CrmGrpcAuthorization.RequiredGuid(r.AccountId);
        var account=await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id&&x.ArchivedAt==null,c.CancellationToken);
        if(account is null)return new AccountSummaryReply { Found=false };
        CrmGrpcAuthorization.Need(account.ExecutiveUserId==actor.UserId ||
                                  actor.Scopes.Contains("crm.account.read-all"));
        return Summary(account);
    }
    public override async Task<AccountSummaryReply> ResolveAccountByTaxId(ResolveAccountByTaxIdRequest r,ServerCallContext c)
    {
        var actor=await CrmGrpcAuthorization.RequireActorAsync(c,cfg,auth);
        CrmGrpcAuthorization.Need(actor.Scopes.Contains("crm.account.read")||
                                  actor.Scopes.Contains("crm.account.read-all"));
        var country=CrmGrpcAuthorization.RequiredGuid(r.CountryId);
        var type=r.TaxIdTypeCode?.Trim();
        var tax=string.IsNullOrWhiteSpace(r.TaxId)?null:
            System.Text.RegularExpressions.Regex.Replace(r.TaxId.Trim().ToUpperInvariant(),@"[\s.\-]","");
        if(string.IsNullOrWhiteSpace(type)||string.IsNullOrWhiteSpace(tax))
            throw new RpcException(new Status(StatusCode.InvalidArgument,"Tax type and identification required"));
        var query=db.Accounts.AsNoTracking().Where(x=>x.CountryId==country&&
            x.TaxIdTypeCode==type&&x.NormalizedTaxId==tax&&x.ArchivedAt==null);
        if(!actor.Scopes.Contains("crm.account.read-all"))
            query=query.Where(x=>x.ExecutiveUserId==actor.UserId);
        var result=await query.FirstOrDefaultAsync(c.CancellationToken);
        return result is null?new AccountSummaryReply {Found=false}:Summary(result);
    }
    private static AccountSummaryReply Summary(Account a) => new() {
        Found=true,AccountId=a.Id.ToString(),DisplayName=a.DisplayName,LegalName=a.LegalName,
        ExecutiveUserId=a.ExecutiveUserId.ToString(),CountryId=a.CountryId?.ToString()??""
    };
}
