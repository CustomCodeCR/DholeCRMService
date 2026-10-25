using Dhole.Crm.Application.Health;
using Dhole.Crm.Contracts.Grpc;
using Grpc.Core;

namespace Dhole.Crm.Api.Grpc;

public sealed class CrmDiagnosticsService(IDependencyVerifier verifier)
    : CrmDiagnostics.CrmDiagnosticsBase
{
    public override async Task<CrmDiagnosticsReply> Check(
        CrmDiagnosticsRequest request, ServerCallContext context)
    {
        var state = await verifier.CheckAsync(context.CancellationToken);
        return new CrmDiagnosticsReply
        {
            Ready = state.IsReady,
            PostgresReachable = state.PostgresReachable,
            RedisReachable = state.RedisReachable,
            CheckedAtUtc = state.CheckedAtUtc.ToString("O")
        };
    }
}
