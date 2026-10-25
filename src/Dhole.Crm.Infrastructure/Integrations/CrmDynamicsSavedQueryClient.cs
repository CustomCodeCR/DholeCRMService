using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Integrations;

/// <summary>
/// Reads ONLY pre-approved Dataverse saved queries via the existing Dynamics connector.
/// No direct OAuth/Dataverse access, SQL/OData pass-through or untrusted destination URL.
/// </summary>
public sealed class CrmDynamicsSavedQueryClient(HttpClient http, IConfiguration configuration)
{
    public sealed record Page(IReadOnlyList<JsonElement> Items, string? NextCursor, int MetadataVersion);

    public async Task<Page> ReadAsync(string? cursor, CancellationToken ct)
    {
        var origin = configuration["Crm:Dynamics:BaseUrl"];
        var key = configuration["Crm:Dynamics:IntegrationKey"];
        var connection = configuration["Crm:Dynamics:ConnectionCode"];
        var name = configuration["Crm:Dynamics:SavedQuery"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(key) ||
            !ValidName(connection) || !ValidName(name))
            throw new InvalidOperationException("dynamics_connector_not_configured");
        if (cursor is { Length: > 4096 })
            throw new InvalidOperationException("dynamics_cursor_invalid");

        var url = new Uri(uri, "/api/v1/keyed/saved-queries/" + Uri.EscapeDataString(name!) + "/execute");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-Dhole-Api-Key", key);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", Guid.NewGuid().ToString("D"));
        request.Content = JsonContent.Create(new
        {
            connectionCode = connection,
            parameters = new Dictionary<string, string>(),
            cursor
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("dynamics_connector_http_" + (int)response.StatusCode,
                null, response.StatusCode);
        // Bounded response body; do not log identifiable Dynamics data.
        await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var values) || values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() > 100)
            throw new InvalidOperationException("dynamics_invalid_saved_query_page");
        var results = values.EnumerateArray().Select(e => e.Clone()).ToArray();
        var next = root.TryGetProperty("nextCursor", out var continuation) &&
                   continuation.ValueKind == JsonValueKind.String ? continuation.GetString() : null;
        if (next is { Length: > 4096 }) throw new InvalidOperationException("dynamics_cursor_invalid");
        var metadataVersion = root.TryGetProperty("metadataVersion", out var version) &&
                              version.TryGetInt32(out var number) ? number : 0;
        return new Page(results, next, metadataVersion);
    }

    private static bool ValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
}

/// <summary>Mappings are operator-supplied, derived from real Dataverse metadata, not guessed new_* fields.</summary>
public sealed record CrmDynamicsAccountCandidate(
    Guid ExternalId, string DisplayName, string LegalName, Guid CountryId,
    string? TaxId, string? TaxIdTypeCode, Guid ExecutiveId,
    string CustomerType, string CategoryType, string CategoryCode,
    string? ExternalVersion, string PayloadHash);

public sealed class CrmDynamicsAccountMapper(IConfiguration configuration)
{
    private string? Field(string key) => configuration["Crm:Dynamics:Fields:" + key];

    public string ConfigurationFingerprint()
    {
        var names = new[] { "ExternalId", "DisplayName", "LegalName", "CountryId",
            "TaxId", "TaxIdTypeCode", "ExecutiveId", "CustomerType", "CategoryType",
            "CategoryCode", "ExternalVersion" };
        // Never hash keys or secret values; field names and policies only.
        var policies = new[] { "CountryMap", "ExecutiveMap", "TaxTypeMap", "CodeMap" }
            .SelectMany(section => configuration.GetSection("Crm:Dynamics:" + section)
                .AsEnumerable()
                .Where(pair => pair.Value is not null)
                .Select(pair => pair.Key + "=" + pair.Value))
            .OrderBy(value => value, StringComparer.Ordinal);
        var spec = string.Join("|", names.Select(n => n + "=" + Field(n))) +
                   "|" + configuration["Crm:Dynamics:SavedQuery"] +
                   "|" + configuration["Crm:Dynamics:ConnectionCode"] +
                   "|" + string.Join("|", policies);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)));
    }

    public bool Configured => new[] { "ExternalId", "DisplayName", "LegalName",
        "CountryId", "ExecutiveId", "CustomerType", "CategoryType", "CategoryCode" }
        .All(key => ValidField(Field(key)));

    public (CrmDynamicsAccountCandidate? Candidate, string? Error) Parse(JsonElement row)
    {
        if (!Configured) return (null, "mapping_not_configured");
        if (row.ValueKind != JsonValueKind.Object) return (null, "invalid_record");
        var external = Value(row, "ExternalId");
        if (!Guid.TryParse(external, out var externalId) || externalId == Guid.Empty)
            return (null, "invalid_external_id");
        var display = Value(row, "DisplayName");
        var legal = Value(row, "LegalName");
        if (string.IsNullOrWhiteSpace(display) || display.Length > 220 ||
            string.IsNullOrWhiteSpace(legal) || legal.Length > 220)
            return (null, "invalid_names");
        var remoteCountry = Value(row, "CountryId");
        var localCountry = string.IsNullOrWhiteSpace(remoteCountry) ? null :
            configuration["Crm:Dynamics:CountryMap:" + remoteCountry];
        if (!Guid.TryParse(localCountry, out var country) || country == Guid.Empty)
            return (null, "country_not_mapped");
        // Dataverse seller identity is explicitly mapped to an Auth UUID; never trust a raw owner ID.
        var externalExecutive = Value(row, "ExecutiveId");
        var localOwner = configuration["Crm:Dynamics:ExecutiveMap:" + externalExecutive];
        if (!Guid.TryParse(localOwner, out var executive) || executive == Guid.Empty)
            return (null, "executive_not_mapped");
        var customer = Resolve("CustomerType", Value(row, "CustomerType"));
        var categoryType = Resolve("CategoryType", Value(row, "CategoryType"));
        var categoryCode = Resolve("CategoryCode", Value(row, "CategoryCode"));
        if (customer is not ("cash" or "credit") ||
            categoryType is not ("CUSTOMER" or "NVOCC") ||
            categoryCode is null || categoryCode.Length != 1 ||
            !(categoryType == "CUSTOMER" ? "ABCD" : "AB").Contains(categoryCode, StringComparison.Ordinal))
            return (null, "categories_not_mapped");
        var tax = Value(row, "TaxId");
        var sourceTaxType = Value(row, "TaxIdTypeCode");
        var taxType = string.IsNullOrWhiteSpace(sourceTaxType) ? null :
            configuration["Crm:Dynamics:TaxTypeMap:" + sourceTaxType];
        if (!string.IsNullOrWhiteSpace(tax) && string.IsNullOrWhiteSpace(taxType))
            return (null, "tax_type_not_mapped");
        if (tax is { Length: > 80 } || taxType is { Length: > 80 })
            return (null, "tax_identity_invalid");
        var etag = Value(row, "ExternalVersion");
        // The payload is hashed for reconciliation. Do not store the raw PII in the job log.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText())));
        return (new(externalId, display.Trim(), legal.Trim(), country, tax,
            taxType, executive, customer, categoryType, categoryCode, etag, hash), null);
    }

    private string? Resolve(string kind, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : configuration["Crm:Dynamics:CodeMap:" + kind + ":" + value];

    private string? Value(JsonElement row, string key)
    {
        var field = Field(key);
        if (!ValidField(field) || !row.TryGetProperty(field!, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool ValidField(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);
}
