using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Crm.Domain;
using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dhole.Crm.Infrastructure.Integrations;

/// <summary>Hacienda verification adapter: Costa Rica only, explicit contract mapping.</summary>
public sealed class CrmHaciendaVerificationClient(HttpClient http, IConfiguration cfg)
{
    public sealed record BusinessActivity(string Code, string? Description);
    public sealed record Verification(string LegalName, IReadOnlyList<BusinessActivity> Activities);

    public Guid? CostaRicaCountryId =>
        Guid.TryParse(cfg["Crm:Hacienda:CostaRicaCountryId"], out var id) && id != Guid.Empty ? id : null;

    public bool Eligible(Account account) =>
        cfg.GetValue("Features:CrmHaciendaValidationEnabled", false) &&
        CostaRicaCountryId.HasValue && account.CountryId == CostaRicaCountryId &&
        !string.IsNullOrWhiteSpace(account.TaxId) &&
        !string.IsNullOrWhiteSpace(account.TaxIdTypeCode) &&
        (cfg["Crm:Hacienda:EligibleTaxTypes"] ?? "").Split(',', StringSplitOptions.TrimEntries)
            .Contains(account.TaxIdTypeCode, StringComparer.OrdinalIgnoreCase);

    public async Task<Verification> VerifyAsync(Account account, CancellationToken ct)
    {
        if (!Eligible(account)) throw new InvalidOperationException("hacienda_account_not_eligible");
        var url = cfg["Crm:Hacienda:ApiUrl"];
        var param = cfg["Crm:Hacienda:IdentificationParameter"];
        var nameField = cfg["Crm:Hacienda:NameField"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.IsLoopback ||
            !endpoint.Host.EndsWith(".go.cr", StringComparison.OrdinalIgnoreCase) ||
            !ValidField(param) || !ValidField(nameField))
            throw new InvalidOperationException("hacienda_contract_not_configured");
        var id = Regex.Replace(account.TaxId!, @"[\s.\-]", "");
        if (!Regex.IsMatch(id, "^[0-9A-Za-z]{5,40}$"))
            throw new InvalidOperationException("hacienda_identifier_invalid");
        var builder = new UriBuilder(endpoint);
        if (!string.IsNullOrEmpty(builder.Query))
            throw new InvalidOperationException("hacienda_query_not_allowed");
        builder.Query = Uri.EscapeDataString(param!) + "=" + Uri.EscapeDataString(id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await http.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("hacienda_http_" + (int)response.StatusCode,
                null, response.StatusCode);
        await response.Content.LoadIntoBufferAsync(256 * 1024, timeout.Token);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameField!, out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 220)
            throw new InvalidOperationException("hacienda_invalid_response");
        var activities = new List<BusinessActivity>();
        var activityField = cfg["Crm:Hacienda:ActivitiesField"];
        var codeField = cfg["Crm:Hacienda:ActivityCodeField"];
        var labelField = cfg["Crm:Hacienda:ActivityDescriptionField"];
        if (ValidField(activityField) && root.TryGetProperty(activityField!, out var array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 100 ||
                !ValidField(codeField))
                throw new InvalidOperationException("hacienda_activities_invalid");
            foreach (var row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(codeField!, out var code) ||
                    code.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    throw new InvalidOperationException("hacienda_activity_code_invalid");
                var value = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 60)
                    throw new InvalidOperationException("hacienda_activity_code_invalid");
                string? label = null;
                if (ValidField(labelField) && row.TryGetProperty(labelField!, out var description) &&
                    description.ValueKind == JsonValueKind.String)
                    label = description.GetString();
                activities.Add(new(value, label is { Length: <= 250 } ? label : null));
            }
        }
        return new(name.GetString()!, activities);
    }

    private static bool ValidField(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, "^[a-zA-Z_][a-zA-Z0-9_]{0,127}$");
}

/// <summary>No Hacienda source overwrites a manually corrected account name.</summary>
public sealed class CrmHaciendaVerificationService(
    CrmDbContext db, CrmHaciendaVerificationClient hacienda)
{
    public async Task ProcessAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(x => x.Id == accountId && x.ArchivedAt == null, ct);
        if (account is null || account.VerificationStatus != "Pending") return;
        if (!hacienda.Eligible(account))
        {
            account.VerificationStatus = "NotApplicable";
            account.UpdatedAt = DateTime.UtcNow;
            account.RowVersion++;
            await db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            var result = await hacienda.VerifyAsync(account, ct);
            var now = DateTime.UtcNow;
            db.HaciendaSnapshots.Add(new CrmHaciendaSnapshot
            {
                AccountId = account.Id,
                ResolvedLegalName = result.LegalName,
                VerifiedAt = now,
                Source = "HACIENDA",
                CreatedByUserId = account.CreatedByUserId
            });
            // Preserve user-edited DisplayName/LegalName. Activities are a source-tagged snapshot.
            foreach (var activity in result.Activities.GroupBy(x => x.Code).Select(x => x.First()))
            {
                var existing = await db.AccountBusinessActivities.FirstOrDefaultAsync(x =>
                    x.AccountId == account.Id && x.ActivityCode == activity.Code, ct);
                if (existing is null)
                    db.AccountBusinessActivities.Add(new AccountBusinessActivity
                    {
                        AccountId = account.Id, ActivityCode = activity.Code,
                        ActivityDescription = activity.Description, Source = "HACIENDA",
                        HaciendaSnapshotAt = now, CreatedByUserId = account.CreatedByUserId
                    });
                else if (existing.Source == "HACIENDA")
                {
                    existing.ActivityDescription = activity.Description;
                    existing.HaciendaSnapshotAt = now;
                    existing.RowVersion++;
                }
            }
            account.VerificationStatus = "Verified";
            account.HaciendaVerifiedAt = now;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Manual registration remains available. Never return "Verified" on provider failure.
            account.VerificationStatus = "Failed";
        }
        account.UpdatedAt = DateTime.UtcNow;
        account.RowVersion++;
        await db.SaveChangesAsync(ct);
    }
}
