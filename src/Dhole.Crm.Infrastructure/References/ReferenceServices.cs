using System.Net;
using System.Net.Http.Json;
using Dhole.Config.Contracts.Grpc;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.References;
public interface IAuthReferenceService
{
    Task<AuthUserReference?> GetActiveAsync(Guid userId, CancellationToken ct);
}
public sealed record AuthUserReference(Guid Id, bool Active, string[] Scopes);
public sealed class AuthReferenceService(HttpClient http, IConfiguration configuration) : IAuthReferenceService
{
    public async Task<AuthUserReference?> GetActiveAsync(Guid userId, CancellationToken ct)
    {
        var address = configuration["Auth:Client:BaseAddress"];
        var key = configuration["Auth:Client:InternalServiceKey"];
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Auth service lookup is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            address.TrimEnd('/') + "/api/internal/auth/crm-users/" + userId);
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthUserReference>(cancellationToken: timeout.Token);
        return result is { Active: true } && result.Id == userId ? result : null;
    }
}
public interface IConfigReferenceService
{
    Task<bool> IsActiveCountryAsync(Guid countryId, CancellationToken ct);
    Task<bool> IsActiveCodeAsync(string catalogGroupSlug, string itemCode, CancellationToken ct);
}
public sealed class ConfigReferenceService(IConfiguration configuration) : IConfigReferenceService
{
    public async Task<bool> IsActiveCodeAsync(string catalogGroupSlug, string itemCode, CancellationToken ct)
    {
        var url = configuration["Grpc:Clients:Config:Address"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Config gRPC endpoint is not configured.");
        using var channel = GrpcChannel.ForAddress(url);
        var client = new ConfigCatalogGrpc.ConfigCatalogGrpcClient(channel);
        var result = await client.GetCatalogItemByCodeAsync(
            new GetCatalogItemByCodeGrpcRequest
            {
                CatalogGroupSlug = catalogGroupSlug,
                CatalogItemCode = itemCode
            }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        return result.Found && result.Item.IsActive &&
               string.Equals(result.Item.CatalogGroupSlug,catalogGroupSlug,StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsActiveCountryAsync(Guid countryId, CancellationToken ct)
    {
        var url = configuration["Grpc:Clients:Config:Address"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Config gRPC address is not configured.");
        using var channel = GrpcChannel.ForAddress(url);
        var client = new ConfigCatalogGrpc.ConfigCatalogGrpcClient(channel);
        var result = await client.GetCatalogItemByIdAsync(new GetCatalogItemByIdGrpcRequest
        {
            CatalogItemId = countryId.ToString()
        }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        return result.Found && result.Item.IsActive &&
               string.Equals(result.Item.CatalogGroupSlug, "countries", StringComparison.OrdinalIgnoreCase);
    }
}
