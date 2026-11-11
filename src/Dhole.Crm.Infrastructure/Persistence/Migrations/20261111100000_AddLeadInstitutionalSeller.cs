using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111100000_AddLeadInstitutionalSeller")]
public sealed class AddLeadInstitutionalSeller : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_leads
    ADD COLUMN IF NOT EXISTS is_castro_fallas_executive boolean NOT NULL DEFAULT FALSE;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep institutional seller assignments if the application is rolled back.
    }
}
