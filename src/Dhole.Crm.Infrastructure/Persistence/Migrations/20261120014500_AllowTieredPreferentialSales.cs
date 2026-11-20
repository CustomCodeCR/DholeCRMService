using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261120014500_AllowTieredPreferentialSales")]
public sealed class AllowTieredPreferentialSales : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // TIERED_SALE is already validated by the domain, but the original PostgreSQL
        // CHECK was never extended when the JSON tier column was introduced.
        // Replacing this constraint preserves every existing agreement and value.
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    DROP CONSTRAINT IF EXISTS ck_crm_preferential_rate_type;
ALTER TABLE crm.crm_account_preferential_rates
    ADD CONSTRAINT ck_crm_preferential_rate_type
    CHECK (rate_type IN (
        'FIXED_AMOUNT', 'DISCOUNT_AMOUNT', 'DISCOUNT_PERCENT',
        'INSURANCE_PERCENT', 'WAIVE', 'NEGOTIATED', 'TIERED_SALE'
    ));
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rolling back would reject agreements that were correctly saved as TIERED_SALE.
        throw new NotSupportedException(
            "Tiered preferential agreements must not be invalidated by a schema rollback.");
    }
}
