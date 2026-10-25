using System.Data;
using System.Text.Json;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Crm.Workers.Followups;

// An independent consumer group never steals events from Pricing's consumer group.
// Events only mark CRM snapshots stale: Pricing retains authority for real statuses.
internal sealed class CrmPricingInboxWorker(IServiceScopeFactory scopes,IConfiguration cfg,
    ILogger<CrmPricingInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            if(!cfg.GetValue("Features:CrmBackgroundTasksEnabled",false))
            {
                await Task.Delay(TimeSpan.FromSeconds(20),ct);
                continue;
            }
            try {await Consume(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogWarning("CRM Pricing inbox stream unavailable ({ErrorType})",ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(15),ct);
            }
        }
    }
    private async Task Consume(CancellationToken ct)
    {
        var connString=cfg["Redis:ConnectionString"];
        if(string.IsNullOrWhiteSpace(connString))
            throw new InvalidOperationException("CRM Redis connection is missing");
        var opts=ConfigurationOptions.Parse(connString);
        opts.ConnectTimeout=5000;opts.AbortOnConnectFail=true;
        using var redis=await ConnectionMultiplexer.ConnectAsync(opts);
        var db=redis.GetDatabase();
        var stream=cfg["Redis:Streams:PricingStreamName"]??"dhole.pricing.events";
        const string group="dhole-crm-pricing-v1";
        const string consumer="crm-inbox";
        try {await db.StreamCreateConsumerGroupAsync(stream,group,"0-0",createStream:true);}
        catch(RedisServerException ex) when(ex.Message.Contains("BUSYGROUP",StringComparison.OrdinalIgnoreCase)){}
        while(!ct.IsCancellationRequested && cfg.GetValue("Features:CrmBackgroundTasksEnabled",false))
        {
            var retry=await db.StreamReadGroupAsync(stream,group,consumer,"0-0",count:30);
            var entries=retry.Length>0?retry:await db.StreamReadGroupAsync(stream,group,consumer,">",count:30);
            if(entries.Length==0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5),ct);continue;
            }
            foreach(var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var fields=entry.Values.ToDictionary(x=>x.Name.ToString(),x=>x.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                if(!TryParsePricingRateChange(fields,out var eventId,out var rateId,out var name))
                {
                    // Other Pricing events do not modify CRM commercial projections.
                    await db.StreamAcknowledgeAsync(stream,group,entry.Id);
                    continue;
                }
                try
                {
                    await using var scope=scopes.CreateAsyncScope();
                    var crm=scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                    await using var tx=await crm.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
                    if(await CrmReminderEngine.Acquire(crm,5012509,ct))
                    {
                        if(!await crm.InboxMessages.AnyAsync(x=>x.EventId==eventId,ct))
                        {
                            var time=DateTime.UtcNow;
                            var quotes=await crm.OpportunityQuotes
                                .Where(x=>x.PricingQuoteId==rateId).ToListAsync(ct);
                            var requests=await crm.OpportunityPricingRequests
                                .Where(x=>x.PricingQuoteId==rateId).ToListAsync(ct);
                            foreach(var q in quotes)
                            {
                                q.PendingRefreshAt=time;q.RowVersion++;q.UpdatedAt=time;
                            }
                            foreach(var r in requests)
                            {
                                r.PendingRefreshAt=time;r.RowVersion++;r.UpdatedAt=time;
                            }
                            crm.InboxMessages.Add(new CrmInboxMessage {
                                EventId=eventId,EventName=name,ProcessedAt=time
                            });
                            await crm.SaveChangesAsync(ct);
                        }
                        await tx.CommitAsync(ct);
                        await db.StreamAcknowledgeAsync(stream,group,entry.Id);
                    }
                }
                catch(Exception ex) when(!ct.IsCancellationRequested)
                {
                    logger.LogWarning("CRM pricing inbox will retry stream entry ({ErrorType})",
                        ex.GetType().Name);
                    // Do not ACK until PostgreSQL commit succeeds; replay is idempotent.
                    break;
                }
            }
        }
    }
    private static bool TryParsePricingRateChange(
        Dictionary<string,string> fields,out Guid eventId,out Guid rateId,out string name)
    {
        eventId=Guid.Empty;rateId=Guid.Empty;name="";
        fields.TryGetValue("messageType",out var eventName);
        eventName ??= fields.GetValueOrDefault("message_type")
            ?? fields.GetValueOrDefault("type");
        if(!string.IsNullOrWhiteSpace(eventName))name=eventName;
        var raw=fields.GetValueOrDefault("payload_json")??
            fields.GetValueOrDefault("payload")??
            fields.GetValueOrDefault("payloadJson")??
            fields.GetValueOrDefault("data");
        if(string.IsNullOrWhiteSpace(raw))return false;
        try
        {
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(string.IsNullOrWhiteSpace(name)&&Find(root,"messageType") is { } type)
                name=type;
            if(name is not ("pricing.rate-header.created" or "pricing.rate-header.updated" or
                "pricing.rate-header.deleted"))return false;
            var eventText=fields.GetValueOrDefault("header:event_id")??
                fields.GetValueOrDefault("event_id")??Find(root,"eventId");
            if(eventText is null && fields.TryGetValue("headers",out var headerJson))
            {
                using var headers=JsonDocument.Parse(headerJson);
                eventText=Find(headers.RootElement,"event_id");
            }
            if(!Guid.TryParse(eventText,out eventId)||eventId==Guid.Empty)return false;
            var rate=Find(root,"id")??Find(root,"rateHeaderId")??Find(root,"rateId");
            return Guid.TryParse(rate,out rateId)&&rateId!=Guid.Empty;
        }
        catch(JsonException){return false;}
    }
    private static string? Find(JsonElement root,string property)
    {
        if(root.ValueKind!=JsonValueKind.Object)return null;
        foreach(var p in root.EnumerateObject())
            if(p.Name.Equals(property,StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind==JsonValueKind.String?p.Value.GetString():null;
        return null;
    }
}
