using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111210000_AddAccountQuoteDeliveryContactIds")]
public sealed class AddAccountQuoteDeliveryContactIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_accounts
              ADD COLUMN IF NOT EXISTS quote_delivery_contact_ids_json jsonb
              NOT NULL DEFAULT '[]'::jsonb;

            -- Preserve every existing single-contact preference.
            UPDATE crm.crm_accounts
            SET quote_delivery_contact_ids_json = jsonb_build_array(quote_delivery_contact_id)
            WHERE quote_delivery_target = 'client'
              AND quote_delivery_contact_id IS NOT NULL
              AND quote_delivery_contact_ids_json = '[]'::jsonb;

            ALTER TABLE crm.crm_accounts DROP CONSTRAINT IF EXISTS crm_account_quote_delivery_valid;
            ALTER TABLE crm.crm_accounts
              ADD CONSTRAINT crm_account_quote_delivery_valid
              CHECK (
                (quote_delivery_target = 'seller'
                    AND quote_delivery_contact_id IS NULL
                    AND quote_delivery_contact_ids_json = '[]'::jsonb)
                OR
                (quote_delivery_target = 'client'
                    AND jsonb_typeof(quote_delivery_contact_ids_json) = 'array'
                    AND jsonb_array_length(quote_delivery_contact_ids_json) > 0)
              );
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve selected recipients and the old primary contact on rollback.
    }
}