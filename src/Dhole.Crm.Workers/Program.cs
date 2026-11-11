using Dhole.Crm.Workers.Realtime;
using Dhole.Crm.Infrastructure.DependencyInjection;
using Dhole.Crm.Workers.Health;
using Dhole.Crm.Workers.Followups;
using Dhole.Crm.Workers;
using CustomCodeFramework.Redis.Streams.Abstractions;

// Container health checks reuse the compiled .NET runtime instead of apt/curl.
if (args.Length == 1 && args[0] == "--container-healthcheck")
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync("http://127.0.0.1:8082/health/ready");
        Environment.Exit(response.IsSuccessStatusCode ? 0 : 1);
    }
    catch
    {
        Environment.Exit(1);
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8082");
builder.Services.AddCrmInfrastructure();
builder.Services.AddCrmReminderRedis(builder.Configuration);
builder.Services.AddHostedService<CrmFollowupSchedulerWorker>();
builder.Services.AddHostedService<CrmReminderDispatcherWorker>();
builder.Services.AddHostedService<CrmPricingInboxWorker>();
// F2 candidate: remains dormant until BOTH release flags explicitly enabled.
builder.Services.AddHostedService<CrmRealtimeOutboxWorker>();
builder.Services.AddHostedService<CrmContractRenewalWorker>();
builder.Services.AddHostedService<CrmCreditReviewWorker>();
builder.Services.AddSingleton<WorkerHeartbeatState>();
builder.Services.AddHostedService<DependencyHeartbeatWorker>();

var app = builder.Build();
// Fail fast: a healthy scheduler without a stream publisher silently loses notifications.
using (var startupScope = app.Services.CreateScope())
    _ = startupScope.ServiceProvider.GetRequiredService<IRedisStreamPublisher>();
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "DholeCRM.Workers" }));
app.MapGet("/health/ready", (WorkerHeartbeatState state) =>
    state.IsReady ? Results.Ok(new { status = "ready" }) :
    Results.Json(new { status = "not_ready" }, statusCode: 503));
app.Run();
