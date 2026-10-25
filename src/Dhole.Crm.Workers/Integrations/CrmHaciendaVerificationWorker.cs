using Dhole.Crm.Infrastructure.Integrations;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Crm.Workers.Integrations;

/// <summary>Fiscal verification never runs for foreign accounts or without approved CR configuration.</summary>
public sealed class CrmHaciendaVerificationWorker(IServiceScopeFactory scopes,
    IConfiguration config, ILogger<CrmHaciendaVerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue("Crm:Hacienda:PollingSeconds", 90), 30, 600)));
        do
        {
            if (!config.GetValue("Features:CrmEnabled", false) ||
                !config.GetValue("Features:CrmBackgroundTasksEnabled", false) ||
                !config.GetValue("Features:CrmHaciendaValidationEnabled", false))
                continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var hacienda = scope.ServiceProvider.GetRequiredService<CrmHaciendaVerificationClient>();
                if (hacienda.CostaRicaCountryId is not Guid cr) continue;
                var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                var ids = await db.Accounts.AsNoTracking().Where(x =>
                    x.CountryId == cr && x.TaxId != null && x.TaxIdTypeCode != null &&
                    x.VerificationStatus == "Pending" && x.ArchivedAt == null)
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(25).ToListAsync(ct);
                foreach (var id in ids)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<CrmHaciendaVerificationService>()
                            .ProcessAsync(id, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception e)
                    {
                        logger.LogWarning("Hacienda verification failed internally for account {AccountId}: {ErrorType}",
                            id, e.GetType().Name);
                        // Account remains Pending if the local DB fails; never claim Verified.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError("Hacienda verifier dispatch failed: {ErrorType}", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
