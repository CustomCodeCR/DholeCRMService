namespace Dhole.Crm.Workers.Health;

public sealed class WorkerHeartbeatState
{
    private long _lastSuccessTicks;
    public void MarkSuccess() => Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
    public bool IsReady
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessTicks);
            return ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromMinutes(2);
        }
    }
}
