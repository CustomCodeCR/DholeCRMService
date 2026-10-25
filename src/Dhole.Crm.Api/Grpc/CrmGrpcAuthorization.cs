using System.Security.Cryptography;
using System.Text;
using Dhole.Crm.Infrastructure.References;
using Grpc.Core;
namespace Dhole.Crm.Api.Grpc;

internal static class CrmGrpcAuthorization
{
    public static async Task<(Guid UserId,HashSet<string> Scopes)> RequireActorAsync(
        ServerCallContext context,IConfiguration configuration,IAuthReferenceService auth)
    {
        // x-dhole-service-key is only for trusted internal gRPC consumers.
        var expected=configuration["Auth:Client:InternalServiceKey"];
        var provided=context.RequestHeaders.FirstOrDefault(x=>x.Key=="x-dhole-service-key")?.Value;
        if(string.IsNullOrWhiteSpace(expected)||string.IsNullOrWhiteSpace(provided))
            throw new RpcException(new Status(StatusCode.Unauthenticated,"Internal identity required"));
        var a=Encoding.UTF8.GetBytes(expected);
        var b=Encoding.UTF8.GetBytes(provided);
        if(a.Length!=b.Length || !CryptographicOperations.FixedTimeEquals(a,b))
            throw new RpcException(new Status(StatusCode.Unauthenticated,"Invalid service identity"));
        var raw=context.RequestHeaders.FirstOrDefault(x=>x.Key=="x-actor-user-id")?.Value;
        if(!Guid.TryParse(raw,out var actor)||actor==Guid.Empty)
            throw new RpcException(new Status(StatusCode.Unauthenticated,"Actor is required"));
        AuthUserReference? user;
        try { user=await auth.GetActiveAsync(actor,context.CancellationToken); }
        catch(Exception) when(!context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,"Auth dependency unavailable"));
        }
        if(user is null)throw new RpcException(new Status(StatusCode.PermissionDenied,"Inactive actor"));
        return (actor,new HashSet<string>(user.Scopes??[],StringComparer.OrdinalIgnoreCase));
    }
    public static bool Allowed(HashSet<string> scopes,string scope) => scopes.Contains(scope);
    public static void Need(bool allowed)
    {
        if(!allowed)throw new RpcException(new Status(StatusCode.PermissionDenied,"CRM scope or ownership required"));
    }
    public static Guid RequiredGuid(string value)
    {
        if(!Guid.TryParse(value,out var id)||id==Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument,"Invalid resource identifier"));
        return id;
    }
}
