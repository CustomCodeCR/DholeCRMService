namespace Dhole.Crm.Domain;
public static class LeadLocationRules
{
    public static bool IsValid(string? address, string? type, decimal? lat, decimal? lon) =>
        !string.IsNullOrWhiteSpace(address) && address.Length <= 4000 &&
        !string.IsNullOrWhiteSpace(type) && type.Length <= 40 &&
        (lat.HasValue == lon.HasValue) &&
        (!lat.HasValue ||
            (lat.Value is >= -90m and <= 90m && lon!.Value is >= -180m and <= 180m));
}
