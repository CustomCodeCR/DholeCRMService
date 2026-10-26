using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Crm.Infrastructure.References;
using Microsoft.Extensions.Caching.Memory;

namespace Dhole.Crm.Api.Endpoints;

/// <summary>Public Hacienda AE lookup proxied through CRM for authorized Costa Rican accounts.</summary>
internal static class HaciendaTaxpayerEndpoints
{
    private const string Endpoint = "https://api.hacienda.go.cr/fe/ae";
    private static readonly SemaphoreSlim FetchGate = new(1, 1);

    public static RouteGroupBuilder MapHaciendaTaxpayers(this RouteGroupBuilder group)
    {
        group.MapGet("/hacienda/lookup", Lookup);
        return group;
    }

    private static async Task<IResult> Lookup(
        HttpContext context, string identificacion, Guid countryId,
        IConfigReferenceService config, IHttpClientFactory clients, IMemoryCache cache,
        CancellationToken ct)
    {
        if (!CrmAccess.Scope(context, "crm.account.create") &&
            !CrmAccess.Scope(context, "crm.account.update") &&
            !CrmAccess.Scope(context, "crm.lead.create"))
            return CrmAccess.Denied();

        if (countryId == Guid.Empty) return Results.BadRequest(new { message = "Seleccione el país." });
        // Normalize separators, but never parse the identification as a number (leading zeros matter).
        var taxId = Regex.Replace(identificacion.Trim(), @"[\s\-]", "");
        if (!Regex.IsMatch(taxId, @"^\d{9,12}$"))
            return Results.BadRequest(new { message = "La identificación debe contener de 9 a 12 dígitos." });

        bool allowedCountry;
        try { allowedCountry = await config.IsCostaRicaCountryAsync(countryId, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, title: "No fue posible validar el país con Config.");
        }
        if (!allowedCountry) return Results.BadRequest(new { message = "Hacienda se consulta únicamente para Costa Rica." });

        var key = "crm:hacienda:ae:" + taxId;
        if (cache.TryGetValue(key, out HaciendaTaxpayer? previous) && previous is not null)
            return Results.Ok(previous);

        await FetchGate.WaitAsync(ct);
        try
        {
            // Double-check under the gate to avoid bursts of repeated queries.
            if (cache.TryGetValue(key, out previous) && previous is not null)
                return Results.Ok(previous);

            using var req = new HttpRequestMessage(HttpMethod.Get,
                Endpoint + "?identificacion=" + Uri.EscapeDataString(taxId));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(9));
            using var response = await clients.CreateClient().SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Results.NotFound(new { message = "La identificación no aparece en Hacienda." });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Results.Problem(statusCode: 503, title: "Hacienda limitó temporalmente las consultas (429). Intente más tarde.");
            if (!response.IsSuccessStatusCode)
                return Results.Problem(statusCode: 502, title: "Hacienda no respondió correctamente.");

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = doc.RootElement;
            string? GetString(string name) => root.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.String ? el.GetString() : null;

            var name = GetString("nombre");
            if (string.IsNullOrWhiteSpace(name))
                return Results.Problem(statusCode: 502, title: "Hacienda devolvió una respuesta sin nombre.");
            var activities = new List<HaciendaActivity>();
            if (root.TryGetProperty("actividades", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var codigo = row.TryGetProperty("codigo", out var c) ? c.ToString() : "";
                    var descripcion = row.TryGetProperty("descripcion", out var d) ? d.ToString() : "";
                    var tipo = row.TryGetProperty("tipo", out var t) ? t.ToString() : "";
                    if (!string.IsNullOrWhiteSpace(codigo))
                        activities.Add(new HaciendaActivity(codigo, descripcion, tipo));
                }
            }
            var item = new HaciendaTaxpayer(taxId, name, GetString("tipoIdentificacion"),
                GetString("regimen"), DateTimeOffset.UtcNow, activities);
            cache.Set(key, item, TimeSpan.FromHours(12));
            return Results.Ok(item);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 504, title: "Tiempo de espera agotado al consultar Hacienda.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(statusCode: 502, title: "No fue posible conectar con Hacienda.");
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 502, title: "Respuesta de Hacienda no válida.");
        }
        finally { FetchGate.Release(); }
    }

    private sealed record HaciendaActivity(string Code, string Description, string Type);
    private sealed record HaciendaTaxpayer(string Identification, string Name,
        string? IdentificationType, string? Regime, DateTimeOffset VerifiedAt,
        IReadOnlyCollection<HaciendaActivity> Activities);
}
