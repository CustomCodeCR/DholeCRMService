using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261117190000_AddOperationsContactFlag")]
public sealed class AddOperationsContactFlag : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_account_contacts
    ADD COLUMN IF NOT EXISTS is_operations_contact boolean NOT NULL DEFAULT false;
CREATE INDEX IF NOT EXISTS ix_crm_account_contacts_account_id_is_operations_contact
    ON crm.crm_account_contacts (account_id, is_operations_contact)
    WHERE active_to IS NULL;
-- Verify/fix historic duplicate principals in staging before applying this invariant.
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_account_contacts_active_primary
    ON crm.crm_account_contacts (account_id)
    WHERE is_primary = true AND active_to IS NULL;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Non-destructive rollback: preserve operational contact assignments.
    }
}
