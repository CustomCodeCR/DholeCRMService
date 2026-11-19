namespace Dhole.Crm.Domain;

/// <summary>
/// Context of one quote charge. All unspecified selectors are genuinely unknown;
/// restricted rates are not matched against missing criteria.
/// </summary>
public sealed record PreferentialRateCriteria(
    string Modality,
    string? LaneCode = null,
    Guid? SourceCostId = null,
    string? ChargeCode = null,
    string? CustomChargeName = null,
    string? EquipmentCode = null,
    string? PolCode = null,
    string? PoeCode = null,
    string? PodCode = null,
    string? CarrierCode = null,
    string? IncotermCode = null,
    string? ServiceCode = null,
    DateTime? AtUtc = null);

public static class CommercialBenefitMatcher
{
    private static string? Code(string? value) => CommercialBenefitRules.NormalizeScopeCode(value);

    private static bool Selector(string[]? codes, string? legacyCode, string? selected)
    {
        var alternatives = codes is { Length: > 0 } ? codes :
            legacyCode is null ? [] : new[] { legacyCode };
        return alternatives.Length == 0 ||
            selected is not null && alternatives.Contains(Code(selected), StringComparer.OrdinalIgnoreCase);
    }

    private static bool Selector(string? ruleValue, string? selected) =>
        ruleValue is null || selected is not null &&
        string.Equals(Code(ruleValue), Code(selected), StringComparison.Ordinal);

    private static bool IsItem(AccountPreferentialRate rate, PreferentialRateCriteria request)
    {
        if (rate.SourceCostId.HasValue)
            return request.SourceCostId == rate.SourceCostId;
        if (string.IsNullOrWhiteSpace(request.ChargeCode)) return false;
        if (!string.Equals(CommercialBenefitRules.CanonicalCharge(request.ChargeCode),
            CommercialBenefitRules.CanonicalCharge(rate.ChargeCode), StringComparison.OrdinalIgnoreCase))
            return false;
        return rate.CustomChargeName is null ||
            string.Equals(rate.CustomChargeName, request.CustomChargeName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(AccountPreferentialRate rate, PreferentialRateCriteria request)
    {
        if (!rate.IsActive || !string.Equals(Code(rate.Modality), Code(request.Modality),
                StringComparison.Ordinal) || !IsItem(rate, request))
            return false;
        var when = request.AtUtc ?? DateTime.UtcNow;
        return (!rate.ValidFrom.HasValue || rate.ValidFrom.Value <= when) &&
            (!rate.ValidUntil.HasValue || rate.ValidUntil.Value >= when) &&
            Selector(rate.LaneCode, request.LaneCode) &&
            Selector(rate.CarrierCode, request.CarrierCode) &&
            Selector(rate.IncotermCode, request.IncotermCode) &&
            Selector(rate.EquipmentCodes, rate.EquipmentCode, request.EquipmentCode) &&
            Selector(rate.PolCodes, rate.OriginCode, request.PolCode) &&
            Selector(rate.PoeCodes, rate.DestinationCode, request.PoeCode) &&
            Selector(rate.PodCodes, null, request.PodCode) &&
            Selector(rate.ServiceCodes, null, request.ServiceCode);
    }

    public static int Specificity(AccountPreferentialRate rate)
    {
        static bool Has(string[]? values, string? legacy = null) =>
            values is { Length: > 0 } || !string.IsNullOrWhiteSpace(legacy);
        return (rate.LaneCode is null ? 0 : 1) +
            (rate.CarrierCode is null ? 0 : 1) +
            (rate.IncotermCode is null ? 0 : 1) +
            (Has(rate.EquipmentCodes, rate.EquipmentCode) ? 1 : 0) +
            (Has(rate.PolCodes, rate.OriginCode) ? 1 : 0) +
            (Has(rate.PoeCodes, rate.DestinationCode) ? 1 : 0) +
            (Has(rate.PodCodes) ? 1 : 0) +
            (Has(rate.ServiceCodes) ? 1 : 0);
    }

    private static int ScopeSize(AccountPreferentialRate rate)
    {
        static int Count(string[]? values, string? legacy = null) =>
            values is { Length: > 0 } ? values.Length : legacy is null ? 0 : 1;
        return Count(rate.EquipmentCodes, rate.EquipmentCode) +
            Count(rate.PolCodes, rate.OriginCode) +
            Count(rate.PoeCodes, rate.DestinationCode) +
            Count(rate.PodCodes) + Count(rate.ServiceCodes);
    }

    // More constrained dimensions win. Equal-dimensional rules with fewer OR values
    // are more precise; stable dates and Id break remaining ties deterministically.
    public static AccountPreferentialRate? Resolve(
        IEnumerable<AccountPreferentialRate> rates, PreferentialRateCriteria request) =>
        rates.Where(rate => Matches(rate, request))
            .OrderByDescending(Specificity)
            .ThenBy(ScopeSize)
            .ThenByDescending(rate => rate.ValidFrom ?? DateTime.MinValue)
            .ThenByDescending(rate => rate.UpdatedAt)
            .ThenBy(rate => rate.Id)
            .FirstOrDefault();
}
