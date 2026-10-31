namespace Dhole.Crm.Domain;

/// <summary>CRM-only negotiated benefits. Pricing is not automatically modified.</summary>
public static class CommercialBenefitRules
{
    public static readonly string[] Modalities = ["FCL", "LCL", "FTL", "LTL", "AIR"];
    // Canonical Pricing CostDetailType values, also present in pricing rate line items.
    public static readonly string[] Charges = [
        "Freight", "OriginCharge", "DestinationCharge", "PortCharge",
        "CustomsCharge", "InlandTransport", "AgentCharge", "Documentation",
        "Insurance", "Other"
    ];
    public static readonly string[] LegacyCharges = [
        "FREIGHT", "PICKUP", "DELIVERY", "ORIGIN", "DESTINATION", "CUSTOMS",
        "INSURANCE", "SED", "HANDLING", "VGM", "TICA", "SEAL",
        "DOCUMENTATION", "FORWARDING", "OTHER"
    ];
    public static readonly string[] RateTypes = [
        "FIXED_AMOUNT", "DISCOUNT_AMOUNT", "DISCOUNT_PERCENT",
        "INSURANCE_PERCENT", "WAIVE", "NEGOTIATED"
    ];

    public static string CanonicalCharge(string? source) => source switch
    {
        "FREIGHT" => "Freight",
        "PICKUP" or "ORIGIN" => "OriginCharge",
        "DELIVERY" or "DESTINATION" => "DestinationCharge",
        "CUSTOMS" => "CustomsCharge",
        "INSURANCE" => "Insurance",
        "DOCUMENTATION" => "Documentation",
        "SED" or "HANDLING" or "VGM" or "TICA" or "SEAL" or
            "FORWARDING" or "OTHER" => "Other",
        _ => source ?? string.Empty
    };

    public static string? NormalizeScopeCode(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim().ToUpperInvariant();

    public static string[] NormalizeSelections(IEnumerable<string>? values) =>
        (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static bool ValidCodes(string[]? codes) =>
        codes is not null && codes.Length <= 60 && codes.All(x =>
            !string.IsNullOrWhiteSpace(x) && x.Length <= 80 &&
            x == x.Trim().ToUpperInvariant() &&
            !x.Contains(',') && !x.Contains(';')) &&
        codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length;

    // A single classification is owned by the Account, not a separate Miami settings record.
    // Accepted only for migrating older records; Account.CategoryType/CategoryCode governs the plan.
    public static bool ValidMiamiPlan(string? value) => value is null ||
        new[] { "A", "B", "C", "D", "NVOCC-A", "NVOCC-B", "LITTLE" }.Contains(value, StringComparer.Ordinal);

    public static string? MiamiTier(string type, string code) =>
        type == "NVOCC" && code is "A" or "B" ? "NVOCC-" + code :
        type == "CUSTOMER" && code is "A" or "B" or "C" or "D" ? code :
        type == "LITTLE" && code == "A" ? "LITTLE" : null;

    public static bool ValidRate(AccountPreferentialRate rule)
    {
        if (!Modalities.Contains(rule.Modality, StringComparer.Ordinal) ||
            !(Charges.Contains(rule.ChargeCode, StringComparer.Ordinal) ||
              LegacyCharges.Contains(rule.ChargeCode, StringComparer.Ordinal)) ||
            !RateTypes.Contains(rule.RateType, StringComparer.Ordinal) ||
            rule.LaneCode is not (null or "MIAMI") ||
            (rule.LaneCode == "MIAMI" && rule.Modality != "LCL") ||
            (CanonicalCharge(rule.ChargeCode) == "Other" && string.IsNullOrWhiteSpace(rule.CustomChargeName)) ||
            rule.CustomChargeName?.Length > 120 ||
            rule.EquipmentCode?.Length > 80 ||
            rule.OriginCode?.Length > 80 || rule.DestinationCode?.Length > 80 ||
            !ValidCodes(rule.EquipmentCodes) || !ValidCodes(rule.PolCodes) ||
            !ValidCodes(rule.PoeCodes) || !ValidCodes(rule.PodCodes) ||
            rule.Notes?.Length > 2000 ||
            (rule.ValidFrom is not null && rule.ValidFrom.Value.Kind != DateTimeKind.Utc) ||
            (rule.ValidUntil is not null && rule.ValidUntil.Value.Kind != DateTimeKind.Utc) ||
            (rule.ValidFrom.HasValue && rule.ValidUntil.HasValue && rule.ValidFrom > rule.ValidUntil) ||
            (rule.Value.HasValue && (rule.Value < 0m || rule.Value > 999999999999m)) ||
            (rule.MinimumCharge.HasValue && (rule.MinimumCharge < 0m || rule.MinimumCharge > 999999999999m)) ||
            (rule.InsuranceCostPercent.HasValue && (rule.InsuranceCostPercent < 0m || rule.InsuranceCostPercent > 100m)) ||
            (rule.InsuranceSalePercent.HasValue && (rule.InsuranceSalePercent < 0m || rule.InsuranceSalePercent > 100m)))
            return false;

        if (rule.RateType == "INSURANCE_PERCENT")
        {
            if (CanonicalCharge(rule.ChargeCode) != "Insurance" || !rule.Value.HasValue ||
                rule.Value > 100m)
                return false;
            // Existing historical records can have neither percent; the original Value
            // was the sale percentage. Newly created benefits must specify both.
            if (rule.InsuranceCostPercent.HasValue != rule.InsuranceSalePercent.HasValue)
                return false;
            if (rule.InsuranceSalePercent.HasValue &&
                rule.Value != rule.InsuranceSalePercent)
                return false;
        }
        else if (rule.InsuranceCostPercent.HasValue || rule.InsuranceSalePercent.HasValue)
            return false;

        if (rule.RateType == "DISCOUNT_PERCENT" &&
            (rule.Value is null || rule.Value > 100m)) return false;
        if (rule.RateType is "FIXED_AMOUNT" or "DISCOUNT_AMOUNT" &&
            rule.Value is null) return false;
        if (rule.RateType is "WAIVE" or "NEGOTIATED" && rule.Value is not null)
            return false;
        if (rule.RateType == "NEGOTIATED" && string.IsNullOrWhiteSpace(rule.Notes))
            return false;
        if (rule.RateType is "WAIVE" or "NEGOTIATED" or "DISCOUNT_PERCENT" &&
            rule.MinimumCharge is not null)
            return false;

        var money = rule.RateType is "FIXED_AMOUNT" or "DISCOUNT_AMOUNT" ||
            rule.MinimumCharge.HasValue;
        if (money && rule.CurrencyCode is not ("USD" or "CRC")) return false;
        if (!money && rule.CurrencyCode is not null) return false;
        if (rule.UnitCode is not null && rule.UnitCode.Length > 32) return false;
        if (rule.RateType == "FIXED_AMOUNT" && string.IsNullOrWhiteSpace(rule.UnitCode))
            return false;
        if (rule.RateType != "FIXED_AMOUNT" && rule.UnitCode is not null)
            return false;
        return true;
    }

    private static string[] Scope(string[]? values, string? legacy) =>
        values is { Length: > 0 } ? values :
        string.IsNullOrWhiteSpace(legacy) ? [] : [legacy];

    private static bool MatchesScope(string[]? a, string? aLegacy,
        string[]? b, string? bLegacy)
    {
        var left = Scope(a, aLegacy);
        var right = Scope(b, bLegacy);
        return left.Length == 0 || right.Length == 0 ||
            left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any();
    }

    public static bool Overlap(AccountPreferentialRate one, AccountPreferentialRate other) =>
        one.Modality == other.Modality && one.LaneCode == other.LaneCode &&
        CanonicalCharge(one.ChargeCode) == CanonicalCharge(other.ChargeCode) &&
        string.Equals(one.CustomChargeName, other.CustomChargeName,
            StringComparison.OrdinalIgnoreCase) &&
        MatchesScope(one.EquipmentCodes, one.EquipmentCode, other.EquipmentCodes, other.EquipmentCode) &&
        MatchesScope(one.PolCodes, one.OriginCode, other.PolCodes, other.OriginCode) &&
        MatchesScope(one.PoeCodes, one.DestinationCode, other.PoeCodes, other.DestinationCode) &&
        MatchesScope(one.PodCodes, null, other.PodCodes, null) &&
        (!one.ValidFrom.HasValue || !other.ValidUntil.HasValue || one.ValidFrom <= other.ValidUntil) &&
        (!other.ValidFrom.HasValue || !one.ValidUntil.HasValue || other.ValidFrom <= one.ValidUntil);
}
