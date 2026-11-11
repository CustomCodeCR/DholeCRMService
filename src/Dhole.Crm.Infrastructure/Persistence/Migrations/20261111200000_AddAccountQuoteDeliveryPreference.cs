using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111200000_AddAccountQuoteDeliveryPreference")]
public sealed class AddAccountQuoteDeliveryPreference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_accounts
              ADD COLUMN IF NOT EXISTS quote_delivery_target character varying(16)
                NOT NULL DEFAULT 'seller',
              ADD COLUMN IF NOT EXISTS quote_delivery_contact_id uuid NULL,
              ADD COLUMN IF NOT EXISTS quote_delivery_updated_at_utc timestamp with time zone NULL;

            ALTER TABLE crm.crm_accounts
              ADD CONSTRAINT crm_account_quote_delivery_valid
              CHECK (
                (quote_delivery_target = 'seller' AND quote_delivery_contact_id IS NULL)
                OR (quote_delivery_target = 'client' AND quote_delivery_contact_id IS NOT NULL)
              );
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve configuration selected by customers even if the application rolls back.
    }
}
