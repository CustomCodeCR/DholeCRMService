using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Routing;

namespace Dhole.Crm.Api.Observability;

/// <summary>
/// Low-cardinality CRM HTTP measurements, with no route IDs, query text,
/// names, tokens, fiscal IDs or personal information in metric dimensions.
/// </summary>
public sealed class CrmRequestTelemetry(RequestDelegate next)
{
    public const string MeterName = "Dhole.Crm.Http";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "dhole.crm.http.requests", description: "CRM HTTP requests");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>(
        "dhole.crm.http.duration", unit: "ms", description: "CRM HTTP request time");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/crm"))
        {
            await next(context);
            return;
        }

        // CRM business and integration responses must not be persisted by browsers/proxies.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? "/api/crm/unmatched";
            // A route template is bounded metadata; never use Request.Path with real GUIDs.
            var tags = new KeyValuePair<string, object?>[]
            {
                new("method", context.Request.Method),
                new("route", route),
                new("status_code", context.Response.StatusCode)
            };
            Requests.Add(1, tags);
            Latency.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, tags);
        }
    }
}
