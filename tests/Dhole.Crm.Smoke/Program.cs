using Dhole.Crm.Contracts.Grpc;
using Dhole.Crm.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Dhole.Crm.Smoke <api-http-url> <api-grpc-url> <worker-http-url>");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
foreach (var (url, endpoint) in new[] { (args[0], "/health/live"), (args[0], "/health/ready"),
                                      (args[2], "/health/live"), (args[2], "/health/ready") })
{
    using var response = await http.GetAsync(url.TrimEnd('/') + endpoint, timeout.Token);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine("HTTP smoke failure: " + endpoint);
        return 1;
    }
}

// Even with CRM feature flags off, the HTTP layer must reject anonymous access.
// The denied response must not be cached by intermediaries or browsers.
using (var denied = await http.GetAsync(
    args[0].TrimEnd('/') + "/api/crm/search?q=smoke", timeout.Token))
{
    if (denied.StatusCode != System.Net.HttpStatusCode.Unauthorized ||
        denied.Headers.CacheControl?.NoStore != true)
    {
        Console.Error.WriteLine("CRM anonymous-access or no-store smoke failed.");
        return 1;
    }
}

using var channel = GrpcChannel.ForAddress(args[1]);
var grpc = new CrmDiagnostics.CrmDiagnosticsClient(channel);
var state = await grpc.CheckAsync(new CrmDiagnosticsRequest(),
    deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: timeout.Token);
if (!state.Ready || !state.PostgresReachable || !state.RedisReachable)
{
    Console.Error.WriteLine("gRPC readiness failed.");
    return 1;
}
// New business gRPC contracts must fail CLOSED when an internal identity is absent.
var accountClient=new CrmAccountReadService.CrmAccountReadServiceClient(channel);
try
{
    await accountClient.GetAccountByIdAsync(new GetAccountByIdRequest {
        AccountId=Guid.NewGuid().ToString(),RequestId=Guid.NewGuid().ToString()
    },deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:timeout.Token);
    Console.Error.WriteLine("Unauthenticated account gRPC call unexpectedly succeeded.");
    return 1;
}
catch(RpcException ex) when(ex.StatusCode==StatusCode.Unauthenticated) {}
var opportunityClient=new CrmOpportunityReadService.CrmOpportunityReadServiceClient(channel);
try
{
    await opportunityClient.GetOpportunityByIdAsync(new GetOpportunityByIdRequest {
        OpportunityId=Guid.NewGuid().ToString(),RequestId=Guid.NewGuid().ToString()
    },deadline:DateTime.UtcNow.AddSeconds(10),cancellationToken:timeout.Token);
    Console.Error.WriteLine("Unauthenticated opportunity gRPC call unexpectedly succeeded.");
    return 1;
}
catch(RpcException ex) when(ex.StatusCode==StatusCode.Unauthenticated) {}
Console.WriteLine("Real HTTP, worker and gRPC readiness smoke tests passed.");
return 0;
