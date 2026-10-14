using CustomCodeFramework.Auth.DependencyInjection;
using Dhole.Crm.Api.Grpc;
using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Application.Health;
using Dhole.Crm.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCrmInfrastructure();
builder.Services.AddCustomCodeAuth(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(8081, listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});
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
app.MapGrpcService<CrmDiagnosticsService>();
app.Run();

public partial class Program { }
