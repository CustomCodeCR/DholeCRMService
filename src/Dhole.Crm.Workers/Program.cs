using Dhole.Crm.Infrastructure.DependencyInjection;
using Dhole.Crm.Workers.Health;
using Dhole.Crm.Workers.Followups;
using Dhole.Crm.Workers;
using CustomCodeFramework.Redis.Streams.Abstractions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8082");
builder.Services.AddCrmInfrastructure();
builder.Services.AddCrmReminderRedis(builder.Configuration);
builder.Services.AddHostedService<CrmFollowupSchedulerWorker>();
builder.Services.AddHostedService<CrmReminderDispatcherWorker>();
builder.Services.AddHostedService<CrmPricingInboxWorker>();
builder.Services.AddHostedService<CrmContractRenewalWorker>();
builder.Services.AddHostedService<CrmCreditReviewWorker>();
builder.Services.AddSingleton<WorkerHeartbeatState>();
builder.Services.AddHostedService<DependencyHeartbeatWorker>();

var app = builder.Build();
// Fail fast: a healthy scheduler without a stream publisher silently loses notifications.
_ = app.Services.GetRequiredService<IRedisStreamPublisher>();
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "DholeCRM.Workers" }));
app.MapGet("/health/ready", (WorkerHeartbeatState state) =>
    state.IsReady ? Results.Ok(new { status = "ready" }) :
    Results.Json(new { status = "not_ready" }, statusCode: 503));
app.Run();
