using Dhole.Crm.Infrastructure.Health;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.IntegrationTests;

[TestClass]
public sealed class DependencyIntegrationTests
{
    [TestMethod]
    public async Task Both_real_infrastructure_dependencies_are_reachable()
    {
        var pg = Environment.GetEnvironmentVariable("CRM_TEST_POSTGRES");
        var redis = Environment.GetEnvironmentVariable("CRM_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(pg) || string.IsNullOrWhiteSpace(redis))
            Assert.Inconclusive("Set CRM_TEST_POSTGRES and CRM_TEST_REDIS to run the live integration test.");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = pg,
                ["Redis:ConnectionString"] = redis
            }).Build();
        var state = await new DependencyVerifier(config).CheckAsync(CancellationToken.None);
        Assert.IsTrue(state.PostgresReachable, "Live PostgreSQL SELECT 1 failed.");
        Assert.IsTrue(state.RedisReachable, "Live Redis PING failed.");
        Assert.IsTrue(state.IsReady);
    }

    [TestMethod]
    public async Task Readiness_fails_closed_if_configuration_is_absent()
    {
        var verifier = new DependencyVerifier(new ConfigurationBuilder().Build());
        var result = await verifier.CheckAsync(CancellationToken.None);
        Assert.IsFalse(result.IsReady);
    }
}
