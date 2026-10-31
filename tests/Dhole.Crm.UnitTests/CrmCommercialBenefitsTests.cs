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
}
