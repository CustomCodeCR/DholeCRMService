using Dhole.Crm.Application.Health;
using Dhole.Crm.Infrastructure.Persistence;
using Dhole.Crm.Infrastructure.References;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Dhole.Crm.Infrastructure.Health;
using Dhole.Crm.Infrastructure.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Crm.Infrastructure.DependencyInjection;

public static class ServiceRegistration
{
    public static IServiceCollection AddCrmInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDependencyVerifier, DependencyVerifier>();
        services.AddDbContext<CrmDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("Postgres")));
        services.AddHttpClient<IAuthReferenceService, AuthReferenceService>();
        services.AddHttpClient<IPricingCommercialClient, PricingCommercialClient>();
        services.AddSingleton<IConfigReferenceService, ConfigReferenceService>();
        return services;
    }
}
