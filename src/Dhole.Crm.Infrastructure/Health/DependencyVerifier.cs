using Dhole.Crm.Application.Health;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;

namespace Dhole.Crm.Infrastructure.Health;

/// <summary>Tests actual dependencies; never returns a simulated OK state.</summary>
public sealed class DependencyVerifier(IConfiguration configuration) : IDependencyVerifier
{
    public async Task<DependencyState> CheckAsync(CancellationToken cancellationToken)
    {
        var database = await CheckPostgresAsync(cancellationToken);
        var redis = await CheckRedisAsync(cancellationToken);
        return new DependencyState(database, redis, DateTimeOffset.UtcNow);
    }

    private async Task<bool> CheckPostgresAsync(CancellationToken ct)
    {
        var value = configuration.GetConnectionString("Postgres")
            ?? configuration["Postgres:ConnectionString"];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            await using var connection = new NpgsqlConnection(value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(timeout.Token);
            return Convert.ToInt32(result) == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> CheckRedisAsync(CancellationToken ct)
    {
        var value = configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var options = ConfigurationOptions.Parse(value);
            options.ConnectTimeout = 5000;
            options.SyncTimeout = 5000;
            options.AbortOnConnectFail = true;
            using var connection = await ConnectionMultiplexer.ConnectAsync(options);
            ct.ThrowIfCancellationRequested();
            var latency = await connection.GetDatabase().PingAsync();
            return latency >= TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
