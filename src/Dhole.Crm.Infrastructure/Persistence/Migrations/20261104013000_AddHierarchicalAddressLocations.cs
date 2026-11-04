using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261104013000_AddHierarchicalAddressLocations")]
public sealed class AddHierarchicalAddressLocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_addresses
    ADD COLUMN IF NOT EXISTS country_code varchar(2),
    ADD COLUMN IF NOT EXISTS admin1_code varchar(160),
    ADD COLUMN IF NOT EXISTS admin1_name varchar(160),
    ADD COLUMN IF NOT EXISTS admin2_code varchar(160),
    ADD COLUMN IF NOT EXISTS admin2_name varchar(160),
    ADD COLUMN IF NOT EXISTS admin3_code varchar(160),
    ADD COLUMN IF NOT EXISTS admin3_name varchar(160),
    ADD COLUMN IF NOT EXISTS postal_code varchar(32);
CREATE INDEX IF NOT EXISTS ix_crm_addresses_country_code_admin1_code
    ON crm.crm_addresses (country_code, admin1_code);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve stored geographic data on rollback, like the CRM history migrations.
    }
}
