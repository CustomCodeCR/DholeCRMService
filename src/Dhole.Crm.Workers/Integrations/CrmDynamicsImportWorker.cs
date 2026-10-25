using Dhole.Crm.Infrastructure.Integrations;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Workers.Integrations;

/// <summary>
/// Durable, page-at-a-time import. Never writes when the feature flag is disabled.
/// Uses the transaction and global advisory lock in the migration service.
/// </summary>
public sealed class CrmDynamicsImportWorker(IServiceScopeFactory scopes,
    IConfiguration config, ILogger<CrmDynamicsImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue("Crm:Dynamics:PollingSeconds", 30), 15, 300)));
        do
        {
            if (!config.GetValue("Features:CrmEnabled", false) ||
                !config.GetValue("Features:CrmBackgroundTasksEnabled", false) ||
                !config.GetValue("Features:CrmDynamicsSyncEnabled", false)) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                var now = DateTime.UtcNow;
                var next = await db.SyncJobs.AsNoTracking()
                    .Where(x => (x.Status == "Pending" || x.Status == "Running") &&
                                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync(ct);
                if (next == Guid.Empty) continue;
                try
                {
                    await scope.ServiceProvider.GetRequiredService<CrmDynamicsMigrationService>()
                        .ProcessNextPageAsync(next, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    // Nothing from the exception message or Dataverse payload enters logs.
                    var code = exception is HttpRequestException ? "connector_http_error" :
                        exception is OperationCanceledException ? "connector_timeout" :
                        exception is InvalidOperationException ? "configuration_or_data_invalid" :
                        "import_batch_failure";
                    logger.LogWarning("CRM Dynamics import page failed: job={JobId} code={Code}", next, code);
                    // Fresh DbContext avoids a poisoned tracked state after transaction failure.
                    await using var failureScope = scopes.CreateAsyncScope();
                    var failureDb = failureScope.ServiceProvider.GetRequiredService<CrmDbContext>();
                    var job = await failureDb.SyncJobs.FirstOrDefaultAsync(x => x.Id == next, ct);
                    if (job is null || job.Status is not ("Pending" or "Running")) continue;
                    job.ErrorCount++;
                    job.AttemptCount++;
                    job.LastErrorCode = code;
                    job.Status = job.AttemptCount >= 5 ? "Failed" : "Pending";
                    var delay = Math.Min(3600, 15 * (1 << Math.Min(job.AttemptCount, 7)));
                    job.NextAttemptAt = job.Status == "Failed" ? null : DateTime.UtcNow.AddSeconds(delay);
                    job.UpdatedAt = DateTime.UtcNow;
                    job.RowVersion++;
                    await failureDb.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError("CRM Dynamics job dispatch failure: {ErrorType}", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
