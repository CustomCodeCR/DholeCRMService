using Dhole.Crm.Application.Health;
using Dhole.Crm.Infrastructure.Health;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Crm.Infrastructure.DependencyInjection;

public static class ServiceRegistration
{
    public static IServiceCollection AddCrmInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDependencyVerifier, DependencyVerifier>();
        return services;
    }
}
