using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261119174500_AddPreferentialCarrierIncotermScope")]
public sealed class AddPreferentialCarrierIncotermScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Backwards-compatible: legacy agreements remain unrestricted (NULL qualifiers).
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_preferential_rates
    ADD COLUMN IF NOT EXISTS carrier_code varchar(80),
    ADD COLUMN IF NOT EXISTS incoterm_code varchar(3);
CREATE INDEX IF NOT EXISTS ix_crm_account_preferential_rates_account_modality_carrier_incoterm
    ON crm.crm_account_preferential_rates (account_id, modality, carrier_code, incoterm_code);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately non-destructive: commercial rate scopes must not be lost on rollback.
        throw new NotSupportedException("Preferential carrier and Incoterm scopes cannot be dropped automatically.");
    }
}
