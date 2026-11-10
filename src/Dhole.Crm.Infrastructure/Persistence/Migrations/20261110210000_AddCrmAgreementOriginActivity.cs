using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CrmDbContext))]
[Migration("20261110210000_AddCrmAgreementOriginActivity")]
public sealed class AddCrmAgreementOriginActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_followups ADD COLUMN IF NOT EXISTS origin_activity_id uuid;
CREATE INDEX IF NOT EXISTS ix_crm_followups_origin_activity_id
    ON crm.crm_followups (origin_activity_id);
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_crm_followups_origin_activity'
    ) THEN
        ALTER TABLE crm.crm_followups
            ADD CONSTRAINT fk_crm_followups_origin_activity
            FOREIGN KEY (origin_activity_id) REFERENCES crm.crm_activities(id)
            ON DELETE RESTRICT;
    END IF;
END $$;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not remove historical parent links on rollback.
    }
}
