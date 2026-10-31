using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261031090000_AddAccountCommercialBenefits")]
public sealed class AddAccountCommercialBenefits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_account_benefit_settings (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    miami_lcl_plan_code varchar(16),
    notes varchar(2000),
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_crm_miami_plan CHECK (
        miami_lcl_plan_code IS NULL OR
        miami_lcl_plan_code IN ('A','B','C','D','NVOCC-A','NVOCC-B','LITTLE'))
);
CREATE UNIQUE INDEX uq_crm_account_benefit_settings_account
    ON crm.crm_account_benefit_settings(account_id);

CREATE TABLE crm.crm_account_preferential_rates (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    modality varchar(12) NOT NULL,
    lane_code varchar(32),
    charge_code varchar(32) NOT NULL,
    custom_charge_name varchar(120),
    rate_type varchar(24) NOT NULL,
    value numeric(18,6),
    minimum_charge numeric(18,6),
    currency_code varchar(3),
    unit_code varchar(32),
    equipment_code varchar(80),
    origin_code varchar(80),
    destination_code varchar(80),
    valid_from timestamptz,
    valid_until timestamptz,
    notes varchar(2000),
    is_active boolean NOT NULL DEFAULT true,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_crm_preferential_modality CHECK (modality IN ('FCL','LCL','FTL','LTL','AIR')),
    CONSTRAINT ck_crm_preferential_rate_type CHECK (rate_type IN ('FIXED_AMOUNT','DISCOUNT_AMOUNT','DISCOUNT_PERCENT','INSURANCE_PERCENT','WAIVE','NEGOTIATED')),
    CONSTRAINT ck_crm_preferential_value CHECK (value IS NULL OR value >= 0),
    CONSTRAINT ck_crm_preferential_minimum CHECK (minimum_charge IS NULL OR minimum_charge >= 0),
    CONSTRAINT ck_crm_preferential_dates CHECK (valid_from IS NULL OR valid_until IS NULL OR valid_from <= valid_until)
);
CREATE INDEX ix_crm_preferential_account_mode_active
    ON crm.crm_account_preferential_rates (account_id, modality, is_active);
CREATE INDEX ix_crm_preferential_account_dates
    ON crm.crm_account_preferential_rates (account_id, valid_from, valid_until);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve contractual history: no implicit destructive rollback.
        throw new NotSupportedException("Commercial customer benefits cannot be dropped automatically.");
    }
}
