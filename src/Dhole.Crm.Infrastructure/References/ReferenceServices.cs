using System.Net;
using System.Net.Http.Json;
using Dhole.Config.Contracts.Grpc;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.References;
public interface IAuthReferenceService
{
    Task<AuthUserReference?> GetActiveAsync(Guid userId, CancellationToken ct);
    Task<AuthUserReference?> GetActiveByUsernameAsync(string username, CancellationToken ct) => Task.FromResult<AuthUserReference?>(null);
    Task<CrmUserDirectoryPage> GetDirectoryAsync(int page,int pageSize,string? search,CancellationToken ct) =>
        Task.FromResult(new CrmUserDirectoryPage(0,page,pageSize,[]));
}
public sealed record AuthUserReference(Guid Id, bool Active, string[] Scopes, string? Email = null, string? DisplayName = null);
public sealed record CrmUserDirectoryItem(Guid Id,string DisplayName,string UserName);
public sealed record CrmUserDirectoryPage(int Total,int Page,int PageSize,CrmUserDirectoryItem[] Items);
public sealed class AuthReferenceService(HttpClient http, IConfiguration configuration) : IAuthReferenceService
{
    public async Task<CrmUserDirectoryPage> GetDirectoryAsync(int page,int pageSize,string? search,CancellationToken ct)
    {
        var address=configuration["Auth:Client:BaseAddress"];
        var key=configuration["Auth:Client:InternalServiceKey"];
        if(string.IsNullOrWhiteSpace(address)||string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Auth service lookup is not configured.");
        var uri=address.TrimEnd('/')+"/api/internal/auth/crm-users/directory?page="+page+
            "&pageSize="+pageSize+"&search="+Uri.EscapeDataString(search??"");
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key",key);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response=await http.SendAsync(request,timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CrmUserDirectoryPage>(cancellationToken:timeout.Token)
            ?? throw new InvalidOperationException("Empty Auth directory response.");
    }


    public async Task<AuthUserReference?> GetActiveByUsernameAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 80)
            return null;
        var address = configuration["Auth:Client:BaseAddress"];
        var key = configuration["Auth:Client:InternalServiceKey"];
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Auth service lookup is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            address.TrimEnd('/') + "/api/internal/auth/crm-users/by-username/" +
            Uri.EscapeDataString(username.Trim()));
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthUserReference>(cancellationToken: timeout.Token);
        return result is { Active: true } ? result : null;
    }

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
public sealed record ActiveCurrencyOption(string Code, string Label);
public interface IConfigReferenceService
{
    Task<bool> IsActiveCountryAsync(Guid countryId, CancellationToken ct);
    Task<bool> IsCostaRicaCountryAsync(Guid countryId, CancellationToken ct);
    Task<bool> IsActiveCodeAsync(string catalogGroupSlug, string itemCode, CancellationToken ct);
    Task<bool> IsActiveCurrencyIsoAsync(string currencyIsoCode, CancellationToken ct);
    Task<IReadOnlyList<ActiveCurrencyOption>> GetActiveCurrencyOptionsAsync(CancellationToken ct);
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

    // Config uses separate internal codes, names, slugs and ISO values.
    // Only explicitly represented, active ISO-4217 values may be selected.
    public async Task<IReadOnlyList<ActiveCurrencyOption>> GetActiveCurrencyOptionsAsync(CancellationToken ct)
    {
        var url = configuration["Grpc:Clients:Config:Address"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Config gRPC endpoint is not configured.");

        using var channel = GrpcChannel.ForAddress(url);
        var client = new ConfigCatalogGrpc.ConfigCatalogGrpcClient(channel);
        var response = await client.GetActiveCatalogItemsByGroupAsync(
            new GetActiveCatalogItemsByGroupGrpcRequest { CatalogGroupSlug = "currencies" },
            deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

        static string? IsoCode(CatalogItemGrpcModel item)
        {
            foreach (var candidate in new[] { item.Value, item.Slug, item.Code })
            {
                var code = candidate?.Trim();
                if (code is { Length: 3 } && code.All(char.IsAsciiLetter))
                    return code.ToUpperInvariant();
            }
            return null;
        }

        return response.Items
            .Where(item => item.IsActive && string.Equals(item.CatalogGroupSlug,
                "currencies", StringComparison.OrdinalIgnoreCase))
            .Select(item => new { Code = IsoCode(item), Item = item })
            .Where(x => x.Code is not null)
            .GroupBy(x => x.Code!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ActiveCurrencyOption(group.Key,
                string.IsNullOrWhiteSpace(group.First().Item.Name)
                    ? group.Key : group.First().Item.Name.Trim()))
            .OrderBy(x => x.Code)
            .ToArray();
    }

    public async Task<bool> IsActiveCurrencyIsoAsync(string currencyIsoCode, CancellationToken ct)
    {
        var iso = currencyIsoCode?.Trim().ToUpperInvariant() ?? "";
        if (iso.Length != 3 || !iso.All(char.IsAsciiLetter))
            return false;
        var options = await GetActiveCurrencyOptionsAsync(ct);
        return options.Any(item => string.Equals(item.Code, iso, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsCostaRicaCountryAsync(Guid countryId, CancellationToken ct)
    {
        var address = configuration["Grpc:Clients:Config:Address"];
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("Config gRPC address is not configured.");
        using var channel = GrpcChannel.ForAddress(address);
        var client = new ConfigCatalogGrpc.ConfigCatalogGrpcClient(channel);
        var result = await client.GetCatalogItemByIdAsync(new GetCatalogItemByIdGrpcRequest {
            CatalogItemId = countryId.ToString()
        }, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        if (!result.Found || !result.Item.IsActive || result.Item.CatalogGroupSlug != "countries")
            return false;
        var country = result.Item;
        return new [] { country.Code, country.Slug, country.Name, country.Value }
            .Any(x => string.Equals(x, "CR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x, "CRI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x, "CRC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x, "Costa Rica", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x, "costa-rica", StringComparison.OrdinalIgnoreCase));
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
