using CustomCodeFramework.Redis.DependencyInjection;
using CustomCodeFramework.Redis.Streams.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Crm.Workers;

/// <summary>
/// Registers both the shared Redis connection and stream publisher.
/// AddCustomCodeRedisStreams alone does not register IConnectionMultiplexer.
/// </summary>
public static class CrmRedisWorkerRegistration
{
    public static IServiceCollection AddCrmReminderRedis(
        this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Redis:ConnectionString"]))
            throw new InvalidOperationException("CRM Redis connection configuration is missing.");

        services.AddCustomCodeRedis(configuration);
        services.AddCustomCodeRedisStreams(configuration);
        return services;
    }
}
