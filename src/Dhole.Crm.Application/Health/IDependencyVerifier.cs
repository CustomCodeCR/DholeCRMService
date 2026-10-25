namespace Dhole.Crm.Application.Health;

public sealed record DependencyState(bool PostgresReachable, bool RedisReachable, DateTimeOffset CheckedAtUtc)
{
    public bool IsReady => PostgresReachable && RedisReachable;
}

public interface IDependencyVerifier
{
    Task<DependencyState> CheckAsync(CancellationToken cancellationToken);
}
