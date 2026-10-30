using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261030140000_AddInstitutionalSellerToAccounts")]
public sealed class AddInstitutionalSellerToAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_accounts
    ADD COLUMN IF NOT EXISTS is_castro_fallas_executive boolean NOT NULL DEFAULT false;
ALTER TABLE crm.crm_account_assignments
    ADD COLUMN IF NOT EXISTS is_castro_fallas_executive boolean NOT NULL DEFAULT false;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve historic seller assignments rather than dropping the institutional flag.
    }
}
