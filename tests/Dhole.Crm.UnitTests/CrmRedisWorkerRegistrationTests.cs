using CustomCodeFramework.Redis.Streams.Abstractions;
using CustomCodeFramework.Redis.Streams.Messages;
using Dhole.Crm.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmRedisWorkerRegistrationTests
{
    [TestMethod]
    public async Task CRM_worker_resolves_publisher_and_publishes_to_real_Redis()
    {
        var redisAddress = Environment.GetEnvironmentVariable("CRM_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(redisAddress))
            Assert.Inconclusive("A real Redis server is required for this integration test.");

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = redisAddress,
                ["Redis:Streams:Enabled"] = "true",
                ["Redis:Streams:DefaultStreamName"] = "dhole.crm.integration",
                ["Redis:Streams:SourceService"] = "DholeCRMService"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCrmReminderRedis(config);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var connection = provider.GetRequiredService<IConnectionMultiplexer>();
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IRedisStreamPublisher>();
        Assert.IsNotNull(publisher);
        Assert.IsTrue(connection.IsConnected, "Redis publisher must use a connected multiplexer.");

        var stream = "dhole.crm.integration." + Guid.NewGuid().ToString("N");
        try
        {
            await publisher.PublishAsync(new RedisStreamMessage
            {
                StreamName = stream,
                MessageType = "notifications.notification.requested",
                Payload = new
                {
                    notificationType = "crm.test.registration",
                    channel = "System",
                    subject = "CRM CI",
                    recipients = new[] { new { userId = Guid.NewGuid().ToString(), address = "" } }
                }
            }, CancellationToken.None);
            Assert.AreEqual(1L, await connection.GetDatabase().StreamLengthAsync(stream));
        }
        finally
        {
            await connection.GetDatabase().KeyDeleteAsync(stream);
        }
    }
}
