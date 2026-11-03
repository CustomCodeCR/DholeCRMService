using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261103195000_AddPreferentialSourceCostReference")]
public sealed class AddPreferentialSourceCostReference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    ADD COLUMN IF NOT EXISTS source_cost_id uuid;
CREATE INDEX IF NOT EXISTS ix_crm_account_preferential_rates_account_id_source_cost_id
    ON crm.crm_account_preferential_rates (account_id, source_cost_id);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the identifier linking the negotiated sale to its original Pricing cost.
        throw new NotSupportedException("Preferential pricing links cannot be removed automatically.");
    }
}
