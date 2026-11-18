using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261118160000_AddCrmFollowupInvitees")]
public sealed class AddCrmFollowupInvitees : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_followup_invitees (
    id uuid PRIMARY KEY,
    followup_id uuid NOT NULL REFERENCES crm.crm_followups(id) ON DELETE RESTRICT,
    user_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    row_version bigint NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_crm_followup_invitees_followup_id_user_id
    ON crm.crm_followup_invitees (followup_id, user_id);
CREATE INDEX IF NOT EXISTS ix_crm_followup_invitees_user_id
    ON crm.crm_followup_invitees (user_id);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Invitations are audit-relevant; do not destructively drop them.
    }
}
