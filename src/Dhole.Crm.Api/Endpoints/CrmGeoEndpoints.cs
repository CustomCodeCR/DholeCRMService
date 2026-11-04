using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

// The CRM only proxies allow-listed geographic sources. No arbitrary user URL is fetched.
// Catalog items are cached, while addresses are always stored in Dhole's own database.
internal static class CrmGeoEndpoints
{
    private sealed record GeoCountry(string Code, string Name);
    private sealed record GeoOption(string Code, string Name);

    public static RouteGroupBuilder MapGeoLocations(this RouteGroupBuilder group)
    {
        group.MapGet("/geo/countries", Countries);
        group.MapGet("/geo/divisions", Divisions);
        return group;
    }

    private static bool Allowed(HttpContext http) =>
        CrmAccess.Scope(http, "crm.account.create") ||
        CrmAccess.Scope(http, "crm.account.update") ||
        CrmDataEndpoints.HasRead(http, "account");

    private static async Task<IResult> Countries(HttpContext http, IHttpClientFactory clients,
        IMemoryCache cache, CancellationToken ct)
    {
        if (!Allowed(http)) return CrmAccess.Denied();
        try { return Results.Ok(await CountryList(clients, cache, ct)); }
        catch (Exception e) when (IsProviderFailure(e)) { return Unavailable(); }
    }

    private static async Task<IResult> Divisions(string countryCode, string? parentCode,
        string? grandparentCode, HttpContext http, IHttpClientFactory clients,
        IMemoryCache cache, CancellationToken ct)
    {
        if (!Allowed(http)) return CrmAccess.Denied();
        var iso = (countryCode ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(iso, "^[A-Z]{2}$") ||
            parentCode is { Length: > 160 } || grandparentCode is { Length: > 160 })
            return CrmDataEndpoints.Bad("Invalid geographic location");
        try
        {
            if (iso == "CR")
            {
                if (string.IsNullOrWhiteSpace(parentCode))
                    return Results.Ok(new { level = "province", options = await CrOptions(
                        clients, cache, "provincias.json", ct), source = "ubicaciones.paginasweb.cr" });
                if (!int.TryParse(parentCode, out var parent) || parent < 1 || parent > 100)
                    return CrmDataEndpoints.Bad("Invalid Costa Rica administrative code");
                if (string.IsNullOrWhiteSpace(grandparentCode))
                    return Results.Ok(new { level = "canton", options = await CrOptions(
                        clients, cache, "provincia/" + parent + "/cantones.json", ct), source = "ubicaciones.paginasweb.cr" });
                if (!int.TryParse(grandparentCode, out var grandparent) || grandparent < 1 || grandparent > 7)
                    return CrmDataEndpoints.Bad("Invalid Costa Rica province");
                return Results.Ok(new { level = "district", options = await CrOptions(
                    clients, cache, "provincia/" + grandparent + "/canton/" + parent + "/distritos.json", ct),
                    source = "ubicaciones.paginasweb.cr" });
            }

            var countries = await CountryList(clients, cache, ct);
            var country = countries.FirstOrDefault(x => x.Code == iso);
            if (country is null) return Results.NotFound();
            var states = await WorldStates(clients, cache, country, ct);
            if (string.IsNullOrWhiteSpace(parentCode))
                return Results.Ok(new { level = "state", options = states, source = "countriesnow.space" });

            var state = states.FirstOrDefault(x => string.Equals(x.Code, parentCode, StringComparison.OrdinalIgnoreCase));
            if (state is null) return CrmDataEndpoints.Bad("Unknown state or province");
            var cities = await Cached(cache, "crm:geo:city:" + iso + ":" + state.Code, async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://countriesnow.space/api/v0.1/countries/state/cities")
                {
                    Content = JsonContent.Create(new { country = country.Name, state = state.Name })
                };
                var data = await Load(clients, request, ct);
                return data.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0 && x.Length <= 160)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .Select(x => new GeoOption(x, x))
                    .ToArray();
            });
            return Results.Ok(new { level = "city", options = cities, source = "countriesnow.space" });
        }
        catch (Exception e) when (IsProviderFailure(e)) { return Unavailable(); }
    }

    private static async Task<GeoCountry[]> CountryList(IHttpClientFactory clients,
        IMemoryCache cache, CancellationToken ct) =>
        await Cached(cache, "crm:geo:countries:v1", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://countriesnow.space/api/v0.1/countries/iso");
            var data = await Load(clients, request, ct);
            return data.EnumerateArray()
                .Select(x => new GeoCountry(x.GetProperty("Iso2").GetString() ?? "",
                    x.GetProperty("name").GetString() ?? ""))
                .Where(x => Regex.IsMatch(x.Code, "^[A-Z]{2}$"))
                .OrderBy(x => x.Name).ToArray();
        });

    private static async Task<GeoOption[]> WorldStates(IHttpClientFactory clients, IMemoryCache cache,
        GeoCountry country, CancellationToken ct) =>
        await Cached(cache, "crm:geo:states:" + country.Code, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://countriesnow.space/api/v0.1/countries/states/q?country=" +
                Uri.EscapeDataString(country.Name));
            var data = await Load(clients, request, ct);
            return data.GetProperty("states").EnumerateArray()
                .Select(x => new GeoOption(x.GetProperty("state_code").GetString() ?? "",
                    x.GetProperty("name").GetString() ?? ""))
                .Where(x => x.Code.Length > 0 && x.Name.Length > 0)
                .OrderBy(x => x.Name).ToArray();
        });

    private static async Task<GeoOption[]> CrOptions(IHttpClientFactory clients, IMemoryCache cache,
        string relativePath, CancellationToken ct) =>
        await Cached(cache, "crm:geo:cr:" + relativePath, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://ubicaciones.paginasweb.cr/" + relativePath);
            var data = await Load(clients, request, ct, envelope: false);
            return data.EnumerateObject()
                .Select(x => new GeoOption(x.Name, x.Value.GetString() ?? ""))
                .Where(x => x.Name.Length > 0 && x.Name.Length <= 3)
                .OrderBy(x => int.TryParse(x.Code, out var code) ? code : int.MaxValue).ToArray();
        });

    private static async Task<JsonElement> Load(IHttpClientFactory clients,
        HttpRequestMessage request, CancellationToken ct, bool envelope = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await clients.CreateClient().SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = json.RootElement;
        if (!envelope) return root.Clone();
        if (root.GetProperty("error").GetBoolean())
            throw new InvalidOperationException("Geographic catalog provider returned an error");
        return root.GetProperty("data").Clone();
    }

    private static async Task<T> Cached<T>(IMemoryCache cache, string key, Func<Task<T>> factory)
        where T : class
    {
        if (cache.TryGetValue<T>(key, out var existing) && existing is not null) return existing;
        var value = await factory();
        cache.Set(key, value, TimeSpan.FromHours(12));
        return value;
    }

    private static bool IsProviderFailure(Exception e) =>
        e is HttpRequestException or JsonException or TaskCanceledException
            or InvalidOperationException or KeyNotFoundException;

    private static IResult Unavailable() => Results.Problem(
        title: "The geographic catalog is temporarily unavailable; use the manual address field",
        statusCode: 503);

    internal static string? PostalCodeForCostaRica(string? countryCode, string? province,
        string? canton, string? district)
    {
        if (!string.Equals(countryCode, "CR", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(province, out var p) || p is < 1 or > 7 ||
            !int.TryParse(canton, out var c) || c is < 1 or > 99 ||
            !int.TryParse(district, out var d) || d is < 1 or > 99) return null;
        return p.ToString("0") + c.ToString("00") + d.ToString("00");
    }
}
