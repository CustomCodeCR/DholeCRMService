using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261120014500_BackfillAgreementFollowupInvitees")]
public sealed class BackfillAgreementFollowupInvitees : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A dated agreement created before this fix inherited the activity's
        // owner but never inherited its internal invitees. Restore only
        // missing links for non-archived open commitments. Never undo an
        // explicit invitation change (Invited/Uninvited in followup history).
        // Do not emit retroactive emails from this data-only migration.
        migrationBuilder.Sql("""
INSERT INTO crm.crm_followup_invitees
    (id, followup_id, user_id, created_by_user_id, created_at, updated_at, row_version)
SELECT gen_random_uuid(), source.followup_id, source.user_id,
       source.created_by_user_id, now(), now(), 1
FROM (
    SELECT DISTINCT f.id AS followup_id, p.user_id, f.created_by_user_id
    FROM crm.crm_followups f
    INNER JOIN crm.crm_activity_participants p
        ON p.activity_id = f.origin_activity_id
    WHERE f.origin_activity_id IS NOT NULL
      AND f.archived_at IS NULL
      AND f.status IN ('Pending', 'Overdue')
      AND p.user_id IS NOT NULL
      AND p.user_id <> f.assigned_user_id
      AND p.role = 'RESPONSIBLE'
      AND NOT EXISTS (
          SELECT 1 FROM crm.crm_followup_history h
          WHERE h.followup_id = f.id AND h.action IN ('Invited', 'Uninvited')
      )
) source
ON CONFLICT (followup_id, user_id) DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve membership, audit history and user invitation decisions.
    }
}
