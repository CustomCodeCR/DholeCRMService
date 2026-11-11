using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111183500_AddAccountQuotationDeliveryPreference")]
public sealed class AddAccountQuotationDeliveryPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_accounts
              ADD COLUMN IF NOT EXISTS quote_delivery_target varchar(10) NOT NULL DEFAULT 'seller',
              ADD COLUMN IF NOT EXISTS quote_delivery_contact_id uuid;

            ALTER TABLE crm.crm_accounts
              ADD CONSTRAINT ck_crm_account_quote_delivery
              CHECK (
                (quote_delivery_target = 'seller' AND quote_delivery_contact_id IS NULL) OR
                (quote_delivery_target = 'client' AND quote_delivery_contact_id IS NOT NULL)
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_accounts DROP CONSTRAINT IF EXISTS ck_crm_account_quote_delivery;
            ALTER TABLE crm.crm_accounts
              DROP COLUMN IF EXISTS quote_delivery_contact_id,
              DROP COLUMN IF EXISTS quote_delivery_target;
            """);
    }
}
