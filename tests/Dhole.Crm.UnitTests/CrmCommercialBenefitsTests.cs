using Dhole.Crm.Domain;

namespace Dhole.Crm.UnitTests;

[TestClass]
public sealed class CrmCommercialBenefitsTests
{
    [TestMethod]
    public void Miami_tiers_match_existing_Miami_customer_groups()
    {
        foreach (var plan in new[] {"A","B","C","D","NVOCC-A","NVOCC-B","LITTLE"})
            Assert.IsTrue(CommercialBenefitRules.ValidMiamiPlan(plan), plan);
        Assert.IsTrue(CommercialBenefitRules.ValidMiamiPlan(null));
        Assert.IsFalse(CommercialBenefitRules.ValidMiamiPlan("PREMIUM"));
    }

    [TestMethod]
    public void All_five_shipping_modalities_accept_pref_rates()
    {
        foreach (var modality in new[] {"FCL","LCL","FTL","LTL","AIR"})
        {
            Assert.IsTrue(CommercialBenefitRules.ValidRate(new AccountPreferentialRate
            {
                Modality = modality, ChargeCode = "FREIGHT", RateType = "FIXED_AMOUNT",
                Value = 175.50m, CurrencyCode = "USD", UnitCode = "SHIPMENT"
            }), modality);
        }
    }

    [TestMethod]
    public void Cargo_insurance_can_have_preferential_percentage_and_minimum()
    {
        var record = new AccountPreferentialRate
        {
            Modality = "LCL", LaneCode = "MIAMI", ChargeCode = "INSURANCE",
            RateType = "INSURANCE_PERCENT", Value = .5m,
            MinimumCharge = 15m, CurrencyCode = "USD"
        };
        Assert.IsTrue(CommercialBenefitRules.ValidRate(record));
        record.Value = 120m;
        Assert.IsFalse(CommercialBenefitRules.ValidRate(record));
        record.Value = -1m;
        Assert.IsFalse(CommercialBenefitRules.ValidRate(record));
        record.Value = .5m;
        record.LaneCode = "MIAMI";
        record.Modality = "FTL";
        Assert.IsFalse(CommercialBenefitRules.ValidRate(record));
    }

    [TestMethod]
    public void Duplicated_active_scope_overlapping_dates_conflicts()
    {
        var first = new AccountPreferentialRate
        {
            Modality = "FCL", ChargeCode = "FREIGHT", EquipmentCode = "40HC",
            ValidFrom = new DateTime(2026,11,1,0,0,0,DateTimeKind.Utc),
            ValidUntil = new DateTime(2026,12,1,0,0,0,DateTimeKind.Utc)
        };
        var later = new AccountPreferentialRate
        {
            Modality = "FCL", ChargeCode = "FREIGHT", EquipmentCode = "40HC",
            ValidFrom = new DateTime(2026,11,20,0,0,0,DateTimeKind.Utc),
            ValidUntil = new DateTime(2026,12,20,0,0,0,DateTimeKind.Utc)
        };
        Assert.IsTrue(CommercialBenefitRules.Overlap(first,later));
        later.ValidFrom = new DateTime(2026,12,2,0,0,0,DateTimeKind.Utc);
        Assert.IsFalse(CommercialBenefitRules.Overlap(first,later));
        later.EquipmentCode = "20DC";
        Assert.IsFalse(CommercialBenefitRules.Overlap(first,later));
    }

    [TestMethod]
    public void No_hidden_discount_percent_above_100_or_negative_amounts()
    {
        var pct = new AccountPreferentialRate
        {
            Modality = "AIR", ChargeCode = "FREIGHT",
            RateType = "DISCOUNT_PERCENT", Value = 101m
        };
        Assert.IsFalse(CommercialBenefitRules.ValidRate(pct));
        pct.Value = 12m;
        Assert.IsTrue(CommercialBenefitRules.ValidRate(pct));
        pct.Value = -2m;
        Assert.IsFalse(CommercialBenefitRules.ValidRate(pct));
    }
    [TestMethod]
    public void Miami_tier_is_derived_from_account_category_not_independent_settings()
    {
        Assert.AreEqual("A", CommercialBenefitRules.MiamiTier("CUSTOMER", "A"));
        Assert.AreEqual("NVOCC-B", CommercialBenefitRules.MiamiTier("NVOCC", "B"));
        Assert.AreEqual("LITTLE", CommercialBenefitRules.MiamiTier("LITTLE", "A"));
        Assert.IsNull(CommercialBenefitRules.MiamiTier("NVOCC", "C"));
    }

    [TestMethod]
    public void Insurance_uses_distinct_cost_and_sale_percentages_on_merchandise_value()
    {
        var rate = new AccountPreferentialRate {
            Modality = "AIR", ChargeCode = "Insurance", RateType = "INSURANCE_PERCENT",
            Value = 0.75m, InsuranceCostPercent = 0.35m,
            InsuranceSalePercent = 0.75m
        };
        Assert.IsTrue(CommercialBenefitRules.ValidRate(rate));
        rate.InsuranceCostPercent = 101m;
        Assert.IsFalse(CommercialBenefitRules.ValidRate(rate));
        rate.InsuranceCostPercent = .35m;
        rate.InsuranceSalePercent = 0.8m;
        Assert.IsFalse(CommercialBenefitRules.ValidRate(rate), "Legacy Value must agree with the current sale percent.");
    }

    [TestMethod]
    public void Multiselect_equipment_and_all_three_port_roles_are_validated()
    {
        var rule = new AccountPreferentialRate {
            Modality = "FCL", ChargeCode = "OriginCharge", CustomChargeName = "Pick up",
            RateType = "FIXED_AMOUNT", Value = 100m, CurrencyCode = "USD",
            UnitCode = "SHIPMENT", EquipmentCodes = ["40HC", "20DC"],
            PolCodes = ["POL-2026-001"], PoeCodes = ["POE-2026-002"],
            PodCodes = ["POD-2026-003"]
        };
        Assert.IsTrue(CommercialBenefitRules.ValidRate(rule));
        rule.PodCodes = ["", "POD-2026-003"];
        Assert.IsFalse(CommercialBenefitRules.ValidRate(rule));
        rule.PodCodes = ["POD-2026-003"];
        rule.EquipmentCodes = ["20DC", "20DC"];
        Assert.IsFalse(CommercialBenefitRules.ValidRate(rule));
    }

    [TestMethod]
    public void Multiselect_overlap_blocks_intersecting_scopes_but_not_unrelated_ports()
    {
        var one = new AccountPreferentialRate {
            Modality = "FCL", ChargeCode = "Freight",
            EquipmentCodes = ["20DC", "40HC"], PolCodes = ["POL-SHA", "POL-YTN"],
            PoeCodes = ["POE-BALBOA"], PodCodes = ["POD-SJO"]
        };
        var same = new AccountPreferentialRate {
            Modality = "FCL", ChargeCode = "FREIGHT",
            EquipmentCodes = ["40HC"], PolCodes = ["POL-YTN"],
            PoeCodes = ["POE-BALBOA"], PodCodes = ["POD-SJO"]
        };
        Assert.IsTrue(CommercialBenefitRules.Overlap(one, same));
        same.PodCodes = ["POD-SAP"];
        Assert.IsFalse(CommercialBenefitRules.Overlap(one, same));
        same.PodCodes = [];
        Assert.IsTrue(CommercialBenefitRules.Overlap(one, same), "Empty selection means all PODs.");
    }

}
