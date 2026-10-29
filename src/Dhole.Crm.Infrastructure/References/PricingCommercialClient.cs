using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.References;

public sealed record PricingRequestSnapshot(Guid Id, Guid? SellerUserId, string Status,
    Guid? RateId, string? ClientName, string? ShipmentMode, DateTime RequestedAtUtc);
public sealed record PricingQuoteSnapshot(Guid Id, string QuoteNumber, string RateCode,
    string Status, string ShipmentMode, Guid? RequestId, Guid? SellerUserId);
public sealed record PricingQuoteRouteSnapshot(
    Guid Id, string? QuoNumber, string? PolName, string? PoeName,
    string? PodName, string? ShipmentMode, string? Status, int? RevisionNumber,
    bool RevisionPending);
public sealed record PricingCreateRequest(
    string Priority, string? ClientName, string? ExecutiveName,
    string? ShipmentMode, string? OriginName, string? DestinationName,
    Guid? PoeId, string? PoeName, Guid? PodId, string? PodName,
    Guid SellerUserId, JsonElement Payload);
public sealed record PricingResult<T>(HttpStatusCode Status,T? Value)
{
    public bool Success => Status is HttpStatusCode.OK or HttpStatusCode.Created && Value is not null;
}
public interface IPricingCommercialClient
{
    Task<PricingResult<PricingRequestSnapshot>> GetRequest(Guid id,string bearer,CancellationToken ct);
    Task<PricingResult<PricingQuoteSnapshot>> GetQuote(Guid id,string bearer,CancellationToken ct);
    Task<PricingResult<PricingQuoteRouteSnapshot>> GetVisibleQuoteRoute(Guid id,string bearer,CancellationToken ct);
    Task<PricingResult<Guid>> CreateRequest(PricingCreateRequest body,
        string bearer,string idempotencyKey,string correlationId,CancellationToken ct);
}
public sealed class PricingCommercialClient(HttpClient client,IConfiguration cfg) : IPricingCommercialClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Uri GetUrl(string path)
    {
        var configured=cfg["Integrations:Pricing:BaseUrl"];
        if(string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(configured,UriKind.Absolute,out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length!=0)
            throw new InvalidOperationException("Pricing internal HTTP endpoint is not configured.");
        return new Uri(configured.TrimEnd('/')+"/"+path.TrimStart('/'));
    }
    private static void SetBearer(HttpRequestMessage req,string token)
    {
        if(string.IsNullOrWhiteSpace(token) ||
            token.Length>12000 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("Missing or invalid bearer token.",nameof(token));
        req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
    }
    private async Task<PricingResult<T>> Send<T>(
        HttpRequestMessage req,CancellationToken ct)
    {
        using(req)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var resp=await client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(!resp.IsSuccessStatusCode)return new PricingResult<T>(resp.StatusCode,default);
                var result=await resp.Content.ReadFromJsonAsync<T>(JsonOptions,timeout.Token);
                return new PricingResult<T>(resp.StatusCode,result);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                return new PricingResult<T>(HttpStatusCode.GatewayTimeout,default);
            }
            catch(HttpRequestException)
            {
                return new PricingResult<T>(HttpStatusCode.BadGateway,default);
            }
            catch(JsonException)
            {
                return new PricingResult<T>(HttpStatusCode.BadGateway,default);
            }
        }
    }
    public Task<PricingResult<PricingRequestSnapshot>> GetRequest(Guid id,string bearer,CancellationToken ct)
    {
        var req=new HttpRequestMessage(HttpMethod.Get,GetUrl("api/pricing/crm/requests/"+id));
        SetBearer(req,bearer);
        return Send<PricingRequestSnapshot>(req,ct);
    }
    public Task<PricingResult<PricingQuoteSnapshot>> GetQuote(Guid id,string bearer,CancellationToken ct)
    {
        var req=new HttpRequestMessage(HttpMethod.Get,GetUrl("api/pricing/crm/quotes/"+id));
        SetBearer(req,bearer);
        return Send<PricingQuoteSnapshot>(req,ct);
    }
    public async Task<PricingResult<PricingQuoteRouteSnapshot>> GetVisibleQuoteRoute(
        Guid id, string bearer, CancellationToken ct)
    {
        // Pricing validates each caller's seller scope and account ownership.
        var seller = new HttpRequestMessage(HttpMethod.Get, GetUrl("api/pricing/seller-rates/" + id));
        SetBearer(seller, bearer);
        var result = await Send<PricingQuoteRouteSnapshot>(seller, ct);
        if (result.Success || (result.Status != HttpStatusCode.Forbidden &&
            result.Status != HttpStatusCode.Unauthorized))
            return result;

        // Pricing staff have rate.view rather than seller-rate-request.create.
        // The fallback is independently authorized by Pricing; CRM only returns
        // route labels from the response, never amounts or supplier costs.
        var staff = new HttpRequestMessage(HttpMethod.Get, GetUrl("api/pricing/rates/" + id));
        SetBearer(staff, bearer);
        return await Send<PricingQuoteRouteSnapshot>(staff, ct);
    }
    public Task<PricingResult<Guid>> CreateRequest(PricingCreateRequest body,
        string bearer,string idempotencyKey,string correlationId,CancellationToken ct)
    {
        var req=new HttpRequestMessage(HttpMethod.Post,GetUrl("api/pricing/rate-requests/"));
        SetBearer(req,bearer);
        req.Headers.TryAddWithoutValidation("Idempotency-Key",idempotencyKey);
        req.Headers.TryAddWithoutValidation("X-Correlation-Id",correlationId);
        req.Content=JsonContent.Create(body,options:JsonOptions);
        return Send<Guid>(req,ct);
    }
}
