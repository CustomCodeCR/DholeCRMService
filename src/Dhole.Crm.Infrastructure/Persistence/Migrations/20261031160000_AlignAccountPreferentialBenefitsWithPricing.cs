using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261031160000_AlignAccountPreferentialBenefitsWithPricing")]
public sealed class AlignAccountPreferentialBenefitsWithPricing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    ADD COLUMN IF NOT EXISTS insurance_cost_percent numeric(9,6),
    ADD COLUMN IF NOT EXISTS insurance_sale_percent numeric(9,6),
    ADD COLUMN IF NOT EXISTS equipment_codes text[] NOT NULL DEFAULT '{}'::text[],
    ADD COLUMN IF NOT EXISTS pol_codes text[] NOT NULL DEFAULT '{}'::text[],
    ADD COLUMN IF NOT EXISTS poe_codes text[] NOT NULL DEFAULT '{}'::text[],
    ADD COLUMN IF NOT EXISTS pod_codes text[] NOT NULL DEFAULT '{}'::text[];

-- Legacy value represented sale percentage only. Do not invent the missing supplier cost.
UPDATE crm.crm_account_preferential_rates
SET insurance_sale_percent = value
WHERE rate_type = 'INSURANCE_PERCENT' AND insurance_sale_percent IS NULL;
UPDATE crm.crm_account_preferential_rates
SET equipment_codes = ARRAY[equipment_code]
WHERE equipment_code IS NOT NULL AND equipment_code <> '' AND cardinality(equipment_codes) = 0;
UPDATE crm.crm_account_preferential_rates
SET pol_codes = ARRAY[origin_code]
WHERE origin_code IS NOT NULL AND origin_code <> '' AND cardinality(pol_codes) = 0;
UPDATE crm.crm_account_preferential_rates
SET poe_codes = ARRAY[destination_code]
WHERE destination_code IS NOT NULL AND destination_code <> '' AND cardinality(poe_codes) = 0;

ALTER TABLE crm.crm_account_preferential_rates
    ADD CONSTRAINT ck_crm_preferential_insurance_cost_pct
      CHECK (insurance_cost_percent IS NULL OR insurance_cost_percent BETWEEN 0 AND 100),
    ADD CONSTRAINT ck_crm_preferential_insurance_sale_pct
      CHECK (insurance_sale_percent IS NULL OR insurance_sale_percent BETWEEN 0 AND 100);

-- Legacy Miami selections remain archived for history; the API never reads them
-- as the account category, which now comes only from crm_accounts.
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Financial agreement data is intentionally retained on rollback.
        throw new NotSupportedException("A customer benefits migration with financial history cannot be automatically reversed.");
    }
}
