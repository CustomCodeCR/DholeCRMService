using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261030160000_AddContactFirstAndLastNames")]
public sealed class AddContactFirstAndLastNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_contacts
    ADD COLUMN IF NOT EXISTS first_name varchar(120),
    ADD COLUMN IF NOT EXISTS last_name varchar(120);
""");
        // Existing full names cannot reliably be split into surnames; leave them untouched.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve actual separate names if an earlier app version is deployed.
    }
}
