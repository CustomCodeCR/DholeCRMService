namespace Dhole.Crm.Domain;

/// <summary>CRM-only validation. Pricing integration is intentionally out of scope.</summary>
public static class CommercialBenefitRules
{
    public static readonly string[] Modalities = ["FCL","LCL","FTL","LTL","AIR"];
    public static readonly string[] MiamiPlans = ["A","B","C","D","NVOCC-A","NVOCC-B","LITTLE"];
    public static readonly string[] Charges =
    [
        "FREIGHT","PICKUP","DELIVERY","ORIGIN","DESTINATION","CUSTOMS",
        "INSURANCE","SED","HANDLING","VGM","TICA","SEAL","DOCUMENTATION","FORWARDING","OTHER"
    ];
    public static readonly string[] RateTypes =
        ["FIXED_AMOUNT","DISCOUNT_AMOUNT","DISCOUNT_PERCENT","INSURANCE_PERCENT","WAIVE","NEGOTIATED"];

    public static bool ValidMiamiPlan(string? value) =>
        value is null || MiamiPlans.Contains(value, StringComparer.Ordinal);

    public static bool ValidRate(AccountPreferentialRate rule)
    {
        if (!Modalities.Contains(rule.Modality, StringComparer.Ordinal) ||
            !Charges.Contains(rule.ChargeCode, StringComparer.Ordinal) ||
            !RateTypes.Contains(rule.RateType, StringComparer.Ordinal) ||
            rule.LaneCode is not (null or "MIAMI") ||
            (rule.LaneCode == "MIAMI" && rule.Modality != "LCL") ||
            (rule.ChargeCode == "OTHER" && string.IsNullOrWhiteSpace(rule.CustomChargeName)) ||
            (rule.ChargeCode != "OTHER" && rule.CustomChargeName is not null) ||
            rule.CustomChargeName?.Length > 120 ||
            rule.EquipmentCode?.Length > 80 ||
            rule.OriginCode?.Length > 80 || rule.DestinationCode?.Length > 80 ||
            rule.Notes?.Length > 2000 ||
            (rule.ValidFrom is not null && rule.ValidFrom.Value.Kind != DateTimeKind.Utc) ||
            (rule.ValidUntil is not null && rule.ValidUntil.Value.Kind != DateTimeKind.Utc) ||
            (rule.ValidFrom.HasValue && rule.ValidUntil.HasValue && rule.ValidFrom > rule.ValidUntil) ||
            (rule.Value.HasValue && (rule.Value < 0m || rule.Value > 999999999999m)) ||
            (rule.MinimumCharge.HasValue && (rule.MinimumCharge < 0m || rule.MinimumCharge > 999999999999m)))
            return false;

        if (rule.RateType == "INSURANCE_PERCENT" && rule.ChargeCode != "INSURANCE")
            return false;
        if (rule.RateType is "DISCOUNT_PERCENT" or "INSURANCE_PERCENT" &&
            (rule.Value is null || rule.Value > 100m))
            return false;
        if (rule.RateType is "FIXED_AMOUNT" or "DISCOUNT_AMOUNT" &&
            rule.Value is null)
            return false;
        if (rule.RateType is "WAIVE" or "NEGOTIATED" && rule.Value is not null)
            return false;
        if (rule.RateType == "NEGOTIATED" && string.IsNullOrWhiteSpace(rule.Notes))
            return false;
        if (rule.RateType is "WAIVE" or "NEGOTIATED" or "DISCOUNT_PERCENT" &&
            rule.MinimumCharge is not null)
            return false;

        var money = rule.RateType is "FIXED_AMOUNT" or "DISCOUNT_AMOUNT" ||
            rule.MinimumCharge.HasValue;
        if (money && rule.CurrencyCode is not ("USD" or "CRC"))
            return false;
        if (!money && rule.CurrencyCode is not null)
            return false;
        if (rule.UnitCode is not null && rule.UnitCode.Length > 32)
            return false;
        if (rule.RateType == "FIXED_AMOUNT" && string.IsNullOrWhiteSpace(rule.UnitCode))
            return false;
        if (rule.RateType != "FIXED_AMOUNT" && rule.UnitCode is not null)
            return false;
        return true;
    }

    public static bool Overlap(AccountPreferentialRate one, AccountPreferentialRate other) =>
        one.Modality == other.Modality && one.LaneCode == other.LaneCode &&
        one.ChargeCode == other.ChargeCode && one.CustomChargeName == other.CustomChargeName &&
        one.EquipmentCode == other.EquipmentCode &&
        one.OriginCode == other.OriginCode && one.DestinationCode == other.DestinationCode &&
        (!one.ValidFrom.HasValue || !other.ValidUntil.HasValue || one.ValidFrom <= other.ValidUntil) &&
        (!other.ValidFrom.HasValue || !one.ValidUntil.HasValue || other.ValidFrom <= one.ValidUntil);
}
