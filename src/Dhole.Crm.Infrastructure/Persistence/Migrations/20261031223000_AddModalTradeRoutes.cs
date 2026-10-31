using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

/// <summary>Preserve the POL, POE and POD of frequently used routes by transport mode.</summary>
[DbContext(typeof(CrmDbContext))]
[Migration("20261031223000_AddModalTradeRoutes")]
public sealed class AddModalTradeRoutes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE crm.crm_customer_trade_profiles
    ADD COLUMN IF NOT EXISTS modality_code varchar(24),
    ADD COLUMN IF NOT EXISTS pol_code varchar(80),
    ADD COLUMN IF NOT EXISTS poe_code varchar(80),
    ADD COLUMN IF NOT EXISTS pod_code varchar(80);
CREATE INDEX IF NOT EXISTS ix_crm_trade_mode_route
    ON crm.crm_customer_trade_profiles(account_id, modality_code, pol_code, poe_code, pod_code);
""");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Route evidence is business history. Do not erase it during rollback.
    }
}
