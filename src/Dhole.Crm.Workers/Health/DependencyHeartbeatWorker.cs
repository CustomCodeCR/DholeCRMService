using Dhole.Crm.Application.Health;

namespace Dhole.Crm.Workers.Health;

/// <summary>Phase 1 infrastructure worker. No business events or fabricated jobs.</summary>
public sealed class DependencyHeartbeatWorker(
    IDependencyVerifier verifier,
    WorkerHeartbeatState state,
    ILogger<DependencyHeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var health = await verifier.CheckAsync(stoppingToken);
            if (health.IsReady) state.MarkSuccess();
            else logger.LogWarning("CRM worker dependency probe failed (Postgres={Postgres}, Redis={Redis})",
                health.PostgresReachable, health.RedisReachable);
        }
        while (await interval.WaitForNextTickAsync(stoppingToken));
    }
}
