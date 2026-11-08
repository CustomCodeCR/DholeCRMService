using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CrmDbContext))]
[Migration("20261107193000_AddCrmActivityRecurrenceAndReminderCadence")]
public sealed class AddCrmActivityRecurrenceAndReminderCadence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_activities
  ADD COLUMN IF NOT EXISTS repeat_every_days integer,
  ADD COLUMN IF NOT EXISTS recurrence_parent_id uuid,
  ADD COLUMN IF NOT EXISTS reminder_repeat_minutes integer;
CREATE UNIQUE INDEX IF NOT EXISTS ix_crm_activities_recurrence_parent_id
  ON crm.crm_activities (recurrence_parent_id)
  WHERE recurrence_parent_id IS NOT NULL;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve historical schedule and reminder metadata.
    }
}
