using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261123170000_AddLeadChangeHistory")]
public sealed class AddLeadChangeHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_lead_change_history (
    id uuid NOT NULL CONSTRAINT pk_crm_lead_change_history_id PRIMARY KEY,
    lead_id uuid NOT NULL REFERENCES crm.crm_leads(id) ON DELETE RESTRICT,
    actor_user_id uuid NULL,
    action_code varchar(24) NOT NULL,
    area_code varchar(24) NOT NULL,
    changes_json jsonb NOT NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_crm_lead_change_history_lead_id_created_at
    ON crm.crm_lead_change_history(lead_id, created_at);

-- Historic creations are authoritative; missing prior field revisions are not invented.
INSERT INTO crm.crm_lead_change_history
    (id, lead_id, actor_user_id, action_code, area_code, changes_json,
     created_by_user_id, updated_by_user_id, created_at, updated_at, row_version)
SELECT l.id, l.id,
       NULLIF(l.created_by_user_id, '00000000-0000-0000-0000-000000000000'::uuid),
       'CREATED', 'LEAD', '[]'::jsonb,
       l.created_by_user_id, NULL, l.created_at, l.created_at, 1
FROM crm.crm_leads l
WHERE NOT EXISTS (
    SELECT 1 FROM crm.crm_lead_change_history h
    WHERE h.lead_id = l.id AND h.action_code = 'CREATED'
)
ON CONFLICT (id) DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve audit data on rollback.
    }
}
