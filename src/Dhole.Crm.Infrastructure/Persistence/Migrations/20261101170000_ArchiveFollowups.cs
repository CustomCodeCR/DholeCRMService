using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261101170000_ArchiveFollowups")]
public sealed class ArchiveFollowups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_followups
    ADD COLUMN IF NOT EXISTS archived_at timestamptz NULL;
CREATE INDEX IF NOT EXISTS ix_crm_followups_active_due
    ON crm.crm_followups(due_at) WHERE archived_at IS NULL;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve archive audit history. Removing archive flags can resurrect tasks.
    }
}
