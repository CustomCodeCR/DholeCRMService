using CustomCodeFramework.Auth.DependencyInjection;
using Dhole.Crm.Api.Grpc;
using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Application.Health;
using Dhole.Crm.Infrastructure.DependencyInjection;

// Container health checks reuse the compiled .NET runtime instead of apt/curl.
if (args.Length == 1 && args[0] == "--container-healthcheck")
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        Environment.Exit(response.IsSuccessStatusCode ? 0 : 1);
    }
    catch
    {
        Environment.Exit(1);
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCrmInfrastructure();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddCustomCodeAuth(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddGrpc();
// Docker compose may explicitly configure Kestrel endpoints. Do not bind 8080/8081 twice.
if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(8080, listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
        options.ListenAnyIP(8081, listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });
}
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "DholeCRM" }));
app.MapGet("/health/ready", async (IDependencyVerifier verifier, CancellationToken ct) =>
{
    var state = await verifier.CheckAsync(ct);
    return Results.Json(new { status = state.IsReady ? "ready" : "not_ready",
        postgres = state.PostgresReachable, redis = state.RedisReachable },
        statusCode: state.IsReady ? 200 : 503);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapCrmDataEndpoints();
app.MapInternalAccountQuoteDelivery();
app.MapGrpcService<CrmDiagnosticsService>();
app.MapGrpcService<CrmAccountReadGrpcService>();
app.MapGrpcService<CrmOpportunityReadGrpcService>();
app.Run();

public partial class Program { }
