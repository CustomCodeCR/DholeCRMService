using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261103233000_AddStructuredAddressLocation")]
public sealed class AddStructuredAddressLocation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_addresses
                ADD COLUMN IF NOT EXISTS country_code varchar(2),
                ADD COLUMN IF NOT EXISTS administrative_area_1 varchar(160),
                ADD COLUMN IF NOT EXISTS administrative_area_2 varchar(160),
                ADD COLUMN IF NOT EXISTS administrative_area_3 varchar(160),
                ADD COLUMN IF NOT EXISTS postal_code varchar(32);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE crm.crm_addresses
                DROP COLUMN IF EXISTS country_code,
                DROP COLUMN IF EXISTS administrative_area_1,
                DROP COLUMN IF EXISTS administrative_area_2,
                DROP COLUMN IF EXISTS administrative_area_3,
                DROP COLUMN IF EXISTS postal_code;
            """);
    }
}
