using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261103211500_AddPreferentialTierSalesJson")]
public sealed class AddPreferentialTierSalesJson : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    ADD COLUMN IF NOT EXISTS tier_sales_json jsonb;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Negotiated tier sale values are customer agreements; do not erase them automatically.
        throw new NotSupportedException("Tiered sales agreements cannot be removed automatically.");
    }
}
