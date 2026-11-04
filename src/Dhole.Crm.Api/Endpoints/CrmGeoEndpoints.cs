using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

// The CRM only proxies allow-listed geographic sources. No arbitrary user URL is fetched.
// Catalog items are cached, while addresses are always stored in Dhole's own database.
internal static class CrmGeoEndpoints
{
    private sealed record GeoCountry(string Code, string Name);
    private sealed record GeoOption(string Code, string Name);
    private sealed record PanamaDivision(
        string ProvinceCode, string ProvinceName,
        string DistrictCode, string DistrictName,
        string CorregimientoCode, string CorregimientoName);

    public static RouteGroupBuilder MapGeoLocations(this RouteGroupBuilder group)
    {
        group.MapGet("/geo/countries", Countries);
        group.MapGet("/geo/divisions", Divisions);
        group.MapGet("/geo/postal-codes", PostalCodes);
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

            // Panama's official administrative hierarchy is Province/Comarca -> District
            // -> Corregimiento. CountriesNow's state/city endpoint cannot supply it.
            if (iso == "PA")
            {
                var divisions = await PanamaLocations(clients, cache, ct);
                const string source = "IGN Panamá · división territorial 2024";
                if (string.IsNullOrWhiteSpace(parentCode))
                    return Results.Ok(new { level = "province", options = divisions
                        .Select(x => new GeoOption(x.ProvinceCode, x.ProvinceName))
                        .DistinctBy(x => x.Code).OrderBy(x => x.Name).ToArray(), source });
                if (string.IsNullOrWhiteSpace(grandparentCode))
                {
                    var districts = divisions.Where(x => x.ProvinceCode == parentCode).ToArray();
                    if (districts.Length == 0) return CrmDataEndpoints.Bad("Unknown Panama province");
                    return Results.Ok(new { level = "district", options = districts
                        .Select(x => new GeoOption(x.DistrictCode, x.DistrictName))
                        .DistinctBy(x => x.Code).OrderBy(x => x.Name).ToArray(), source });
                }
                var corregimientos = divisions
                    .Where(x => x.ProvinceCode == grandparentCode && x.DistrictCode == parentCode)
                    .ToArray();
                if (corregimientos.Length == 0)
                    return CrmDataEndpoints.Bad("Unknown Panama district");
                return Results.Ok(new { level = "corregimiento", options = corregimientos
                    .Select(x => new GeoOption(x.CorregimientoCode, x.CorregimientoName))
                    .DistinctBy(x => x.Code).OrderBy(x => x.Name).ToArray(), source });
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

    // Official IGN Tommy Guardia polygon attributes contain national IDs for all three
    // levels. Cache the small attributes-only response; never expose provider URLs
    // supplied by clients or depend on CountriesNow for Panama.
    private static async Task<PanamaDivision[]> PanamaLocations(IHttpClientFactory clients,
        IMemoryCache cache, CancellationToken ct) =>
        await Cached(cache, "crm:geo:panama:ign-2024:v1", async () =>
        {
            const string url =
                "https://services6.arcgis.com/LC15PAkubfypkSFO/arcgis/rest/services/" +
                "corregimientos_panama/FeatureServer/0/query?where=1%3D1" +
                "&outFields=cod_prov%2Cnomb_prov%2Ccod_dist%2Cnomb_dist%2Ccod_corr%2Cnomb_corr" +
                "&returnGeometry=false&resultRecordCount=2000&f=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var document = await Load(clients, request, ct, envelope: false);
            if (document.TryGetProperty("error", out _))
                throw new InvalidOperationException("Panama geographic catalog returned an error");
            var entries = document.GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("attributes"))
                .Select(attributes => new PanamaDivision(
                    PanamaValue(attributes, "cod_prov"), PanamaName(attributes, "nomb_prov"),
                    PanamaValue(attributes, "cod_dist"), PanamaName(attributes, "nomb_dist"),
                    PanamaValue(attributes, "cod_corr"), PanamaName(attributes, "nomb_corr")))
                .Where(x => x.ProvinceCode.Length > 0 && x.ProvinceName.Length > 0 &&
                    x.DistrictCode.Length > 0 && x.DistrictName.Length > 0 &&
                    x.CorregimientoCode.Length > 0 && x.CorregimientoName.Length > 0)
                .DistinctBy(x => x.CorregimientoCode).ToArray();
            // A partial feature layer result must not become the definitive list.
            if (entries.Length < 500)
                throw new InvalidOperationException("Panama geographic catalog is incomplete");
            return entries;
        });

    private static string PanamaValue(JsonElement data, string field) =>
        data.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : value.ToString()).Trim()
            : "";

    private static string PanamaName(JsonElement data, string field)
    {
        var raw = PanamaValue(data, field);
        var name = CultureInfo.GetCultureInfo("es-PA").TextInfo.ToTitleCase(
            raw.ToLower(CultureInfo.GetCultureInfo("es-PA")));
        return Regex.Replace(name, @"\bPanama\b", "Panamá",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }


    // Postal codes vary by address in many countries. Suggest known codes only,
    // without silently inventing one from an administrative division.
    // Requests contain PUBLIC place names only; never send street addresses.
    private static async Task<IResult> PostalCodes(
        string countryCode, string? locality, string? region,
        HttpContext http, IHttpClientFactory clients, IMemoryCache cache,
        CancellationToken ct)
    {
        if (!Allowed(http)) return CrmAccess.Denied();
        var iso = (countryCode ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(iso, "^[A-Z]{2}$") ||
            locality is { Length: > 160 } || region is { Length: > 160 })
            return CrmDataEndpoints.Bad("Invalid postal code search");

        // Costa Rica has a deterministic district code (existing CRM calculation).
        // Panama's 2026 official postal system is geolocated down to the building;
        // a corregimiento alone is insufficient to identify the correct code.
        if (iso is "CR" or "PA" || string.IsNullOrWhiteSpace(locality))
            return Results.Ok(new { options = Array.Empty<GeoOption>(),
                source = iso == "PA" ? "correospanama.gob.pa" : "none" });

        var place = locality.Trim();
        var area = region?.Trim() ?? "";
        try
        {
            var choices = await Cached(cache,
                "crm:geo:postal:v1:" + iso + ":" + place.ToUpperInvariant() + ":" +
                area.ToUpperInvariant(), async () =>
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                    Uri.EscapeDataString(place) + "&count=30&language=es&countryCode=" + iso;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var data = await Load(clients, request, ct, envelope: false);
                if (!data.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array) return Array.Empty<GeoOption>();
                var found = new List<GeoOption>();
                foreach (var entry in results.EnumerateArray())
                {
                    if (!entry.TryGetProperty("country_code", out var country) ||
                        !string.Equals(country.GetString(), iso, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (PostalMatchName(place) != PostalMatchName(name)) continue;
                    var admin1 = entry.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                    if (area.Length > 0 && admin1.Length > 0 &&
                        PostalMatchName(area) != PostalMatchName(admin1))
                        continue;
                    if (!entry.TryGetProperty("postcodes", out var codes) ||
                        codes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var code in codes.EnumerateArray())
                    {
                        if (code.ValueKind != JsonValueKind.String) continue;
                        var postal = (code.GetString() ?? "").Trim();
                        if (postal.Length is > 0 and <= 32)
                            found.Add(new GeoOption(postal, postal));
                    }
                }
                return found.DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            });
            return Results.Ok(new { options = choices, source = "GeoNames / Open-Meteo" });
        }
        catch (Exception e) when (IsProviderFailure(e)) { return Unavailable(); }
    }

    private static string PostalMatchName(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD),
            @"\p{Mn}", "");

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


    // Embedded territorial registry (IGN 2026). Do not require a third-party HTTP
    // connection to populate the 7 provinces, 84 cantons and 494 districts.
    private static readonly Lazy<JsonDocument> CostaRicaRegistry = new(() =>
    {
        using var stream = typeof(CrmGeoEndpoints).Assembly.GetManifestResourceStream(
            "Dhole.Crm.Api.Data.CostaRica2026.json")
            ?? throw new InvalidOperationException("Missing embedded Costa Rica territorial registry");
        return JsonDocument.Parse(stream);
    });

    private static GeoOption[] CrLocalOptions(string relativePath)
    {
        var provinces = CostaRicaRegistry.Value.RootElement.GetProperty("provincias");
        if (relativePath == "provincias.json")
            return provinces.EnumerateArray()
                .Select(p => new GeoOption(p.GetProperty("codigo").GetString()!,
                    p.GetProperty("nombre").GetString()!)).ToArray();

        var segments = relativePath.Split('/');
        if (segments.Length is not (3 or 5) || segments[0] != "provincia" ||
            !int.TryParse(segments[1], out var provinceCode) || provinceCode is < 1 or > 7)
            return [];
        var province = provinces.EnumerateArray().FirstOrDefault(p =>
            p.GetProperty("codigo").GetString() == provinceCode.ToString());
        if (province.ValueKind != JsonValueKind.Object) return [];
        var cantons = province.GetProperty("cantones");

        if (segments.Length == 3 && segments[2] == "cantones.json")
            return cantons.EnumerateArray()
                .Select(c => new GeoOption(
                    int.Parse(c.GetProperty("codigo").GetString()![1..]).ToString(),
                    c.GetProperty("nombre").GetString()!)).ToArray();

        if (segments.Length != 5 || segments[2] != "canton" ||
            segments[4] != "distritos.json" ||
            !int.TryParse(segments[3], out var cantonCode) || cantonCode is < 1 or > 99)
            return [];
        var fullCode = provinceCode.ToString() + cantonCode.ToString("00");
        var canton = cantons.EnumerateArray().FirstOrDefault(c =>
            c.GetProperty("codigo").GetString() == fullCode);
        if (canton.ValueKind != JsonValueKind.Object) return [];
        return canton.GetProperty("distritos").EnumerateArray()
            .Select(d => new GeoOption(
                int.Parse(d.GetProperty("codigo").GetString()![3..]).ToString(),
                d.GetProperty("nombre").GetString()!)).ToArray();
    }

    private static Task<GeoOption[]> CrOptions(IHttpClientFactory clients, IMemoryCache cache,
        string relativePath, CancellationToken ct)
    {
        // Sourced from IGN 2026 via ryze-cr/ubicaciones-cr (MIT).
        // This data ships with the service and remains available during provider outages.
        return Task.FromResult(CrLocalOptions(relativePath));
    }

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
