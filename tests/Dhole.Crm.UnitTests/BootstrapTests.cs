using Dhole.Crm.Application.Health;
using Dhole.Crm.Contracts.Grpc;
using Dhole.Crm.Workers.Health;
using Google.Protobuf;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class BootstrapTests
{
    [DataTestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    public void Readiness_requires_both_dependencies(bool pg, bool redis, bool ready)
    {
        Assert.AreEqual(ready, new DependencyState(pg, redis, DateTimeOffset.UtcNow).IsReady);
    }

    [TestMethod]
    public void Worker_stays_unready_until_actual_success()
    {
        var state = new WorkerHeartbeatState();
        Assert.IsFalse(state.IsReady);
        state.MarkSuccess();
        Assert.IsTrue(state.IsReady);
    }

    [TestMethod]
    public void Protobuf_contract_roundtrips()
    {
        var reply = new CrmDiagnosticsReply
        {
            Ready = true, PostgresReachable = true, RedisReachable = true,
            CheckedAtUtc = "2026-10-14T18:00:00Z"
        };
        var decoded = CrmDiagnosticsReply.Parser.ParseFrom(reply.ToByteArray());
        Assert.AreEqual(reply, decoded);
    }
}
