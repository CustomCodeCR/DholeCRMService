using Dhole.Crm.Contracts.Grpc;
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

using var channel = GrpcChannel.ForAddress(args[1]);
var grpc = new CrmDiagnostics.CrmDiagnosticsClient(channel);
var state = await grpc.CheckAsync(new CrmDiagnosticsRequest(),
    deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: timeout.Token);
if (!state.Ready || !state.PostgresReachable || !state.RedisReachable)
{
    Console.Error.WriteLine("gRPC readiness failed.");
    return 1;
}
Console.WriteLine("Real HTTP, worker and gRPC readiness smoke tests passed.");
return 0;
