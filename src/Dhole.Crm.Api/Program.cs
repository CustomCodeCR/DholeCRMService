using CustomCodeFramework.Auth.DependencyInjection;
using Dhole.Crm.Api.Grpc;
using Dhole.Crm.Api.Endpoints;
using Dhole.Crm.Api.Observability;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Dhole.Crm.Application.Health;
using Dhole.Crm.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCrmInfrastructure();
builder.Services.AddHttpClient();
builder.Services.AddCustomCodeAuth(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("crm-api", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("user_id")?.Value
            ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, AutoReplenishment = true
        }));
});
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
app.UseRouting();
app.UseMiddleware<CrmRequestTelemetry>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapCrmDataEndpoints();
app.MapGrpcService<CrmDiagnosticsService>();
app.MapGrpcService<CrmAccountReadGrpcService>();
app.MapGrpcService<CrmOpportunityReadGrpcService>();
app.Run();

public partial class Program { }
