using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261119195000_AddPreferentialServiceScope")]
public sealed class AddPreferentialServiceScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    ADD COLUMN IF NOT EXISTS service_codes text[] NOT NULL DEFAULT ARRAY[]::text[];
CREATE INDEX IF NOT EXISTS ix_crm_account_preferential_rates_account_modality_service_codes
    ON crm.crm_account_preferential_rates USING GIN (service_codes);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Non-destructive rollback to avoid losing negotiated service restrictions.
        throw new NotSupportedException("Preferential service scopes cannot be dropped automatically.");
    }
}
