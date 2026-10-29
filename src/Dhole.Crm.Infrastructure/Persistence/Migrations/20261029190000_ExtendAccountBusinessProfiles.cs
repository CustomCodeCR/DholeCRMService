using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261029190000_ExtendAccountBusinessProfiles")]
public sealed class ExtendAccountBusinessProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS active_services varchar(2000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS presence_countries varchar(2000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS commercial_strategy varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS projects_of_interest varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS pricing_observations varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS tariff_monitoring_notes varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS tariff_sending_notes varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS customs_instructions varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS cargo_collection_instructions varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS document_review_instructions varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS assigned_operations varchar(500);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS competitor_observations varchar(4000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS trade_agreement_notes varchar(2000);
ALTER TABLE crm.crm_account_business_profiles ADD COLUMN IF NOT EXISTS web_customer_reference varchar(200);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS web_customer_reference;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS trade_agreement_notes;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS competitor_observations;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS assigned_operations;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS document_review_instructions;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS cargo_collection_instructions;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS customs_instructions;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS tariff_sending_notes;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS tariff_monitoring_notes;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS pricing_observations;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS projects_of_interest;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS commercial_strategy;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS presence_countries;
ALTER TABLE crm.crm_account_business_profiles DROP COLUMN IF EXISTS active_services;
""");
    }
}
