using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Crm.Workers.Realtime;

/// <summary>
/// Opt-in F2 publisher. Both release-wide AND CRM-local flags are required.
/// Never enabled by default. Safe to register before the coordinated batch.
/// </summary>
internal sealed class CrmRealtimeOutboxWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<CrmRealtimeOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (configuration.GetValue("Features:UniversalRealtimeEnabled", false) &&
                configuration.GetValue("Realtime:Outbox:Enabled", false))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                    var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                    var writer = new CrmRedisRealtimeStreamWriter(redis.GetDatabase());
                    var processor = new CrmRealtimeOutboxProcessor(
                        db.Database.GetDbConnection().ConnectionString, writer, logger);
                    var result = await processor.DispatchOnceAsync(
                        configuration.GetValue("Realtime:Outbox:BatchSize", 50), ct);
                    if (result.Claimed > 0)
                        logger.LogInformation(
                            "CRM realtime outbox: claimed={Claimed}, published={Published}, retried={Retried}, deadletter={DeadLetter}",
                            result.Claimed, result.Published, result.Retried, result.DeadLettered);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Redis failures do not propagate to REST operations.
                    logger.LogWarning("CRM realtime outbox paused after {FailureType}",
                        ex.GetType().Name);
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(
                    Math.Clamp(configuration.GetValue("Realtime:Outbox:IntervalSeconds", 5), 2, 60)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
