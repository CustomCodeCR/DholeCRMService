using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261114140000_AddFollowupStartAt")]
public sealed class AddFollowupStartAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_followups
    ADD COLUMN IF NOT EXISTS start_at timestamp with time zone;
CREATE INDEX IF NOT EXISTS ix_crm_followups_start_at
    ON crm.crm_followups (start_at);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve users' scheduled interval start timestamps.
    }
}
