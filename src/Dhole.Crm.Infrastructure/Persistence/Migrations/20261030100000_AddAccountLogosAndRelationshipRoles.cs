using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261030100000_AddAccountLogosAndRelationshipRoles")]
public sealed class AddAccountLogosAndRelationshipRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_accounts
    ADD COLUMN IF NOT EXISTS logo_file_id uuid;
ALTER TABLE crm.crm_accounts
    ALTER COLUMN relationship_type_code TYPE varchar(256);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Never silently truncate previously saved multi-selection data or remove logo references.
    }
}
