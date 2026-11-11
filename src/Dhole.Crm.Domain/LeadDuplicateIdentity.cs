using System.Text.RegularExpressions;

namespace Dhole.Crm.Domain;

/// <summary>
/// Business identity used to reject existing prospects regardless of which seller owns them.
/// Tax ID has priority; normalized exact business names are the fallback.
/// </summary>
public static class LeadDuplicateIdentity
{
    public static string NormalizeName(string? value) =>
        Regex.Replace((value ?? "").Trim(), @"\s+", " ").ToUpperInvariant();

    public static string NormalizeTax(string? value) =>
        Regex.Replace((value ?? "").Trim(), @"[\s.\-]", "").ToUpperInvariant();

    public static string[] LockKeys(string name, string? companyName, Guid? countryId, string? taxId)
    {
        var country = countryId?.ToString("N") ?? "*";
        var keys = new[] {
            $"crm:lead:name:{country}:{NormalizeName(name)}",
            $"crm:lead:name:{country}:{NormalizeName(companyName)}",
            $"crm:lead:tax:{country}:{NormalizeTax(taxId)}"
        };
        return keys.Where(x => !x.EndsWith(':')).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public static bool Matches(Lead existing, string name, string? companyName, Guid? countryId, string? taxId)
    {
        if (existing.ArchivedAt is not null) return false;
        if (existing.CountryId.HasValue && countryId.HasValue && existing.CountryId != countryId)
            return false;
        var tax = NormalizeTax(taxId);
        if (tax.Length > 0 && tax == NormalizeTax(existing.TaxId)) return true;
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        var normalizedName = NormalizeName(name);
        var normalizedCompany = NormalizeName(companyName);
        if (normalizedName.Length > 0) incoming.Add(normalizedName);
        if (normalizedCompany.Length > 0) incoming.Add(normalizedCompany);
        return incoming.Contains(NormalizeName(existing.Name)) ||
               (NormalizeName(existing.CompanyName) is var company && company.Length > 0 && incoming.Contains(company));
    }
}
