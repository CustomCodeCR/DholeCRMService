using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261031023000_AddCrmAgendaReminders")]
public sealed class AddCrmAgendaReminders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_activities
  ADD COLUMN IF NOT EXISTS reminder_lead_minutes integer,
  ADD COLUMN IF NOT EXISTS reminder_channel_strategy varchar(12);
ALTER TABLE crm.crm_followups
  ADD COLUMN IF NOT EXISTS subject varchar(250),
  ADD COLUMN IF NOT EXISTS reminder_lead_minutes integer,
  ADD COLUMN IF NOT EXISTS reminder_channel_strategy varchar(12);
ALTER TABLE crm.crm_activities
  ADD CONSTRAINT ck_crm_activities_reminder_minutes
  CHECK (reminder_lead_minutes IS NULL OR reminder_lead_minutes BETWEEN 0 AND 10080);
ALTER TABLE crm.crm_followups
  ADD CONSTRAINT ck_crm_followups_reminder_minutes
  CHECK (reminder_lead_minutes IS NULL OR reminder_lead_minutes BETWEEN 0 AND 10080);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not destroy CRM agenda settings or notification scheduling history.
    }
}
