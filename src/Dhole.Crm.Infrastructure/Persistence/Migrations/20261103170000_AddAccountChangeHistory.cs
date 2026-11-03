using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261103170000_AddAccountChangeHistory")]
public sealed class AddAccountChangeHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_account_change_history (
    id uuid NOT NULL CONSTRAINT pk_crm_account_change_history_id PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
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
CREATE INDEX IF NOT EXISTS ix_crm_account_change_history_account_id_created_at
    ON crm.crm_account_change_history(account_id, created_at);

-- Existing accounts retain their real stored creation date and creator.
-- Earlier field-by-field revisions cannot be reconstructed reliably.
INSERT INTO crm.crm_account_change_history
    (id, account_id, actor_user_id, action_code, area_code, changes_json,
     created_by_user_id, updated_by_user_id, created_at, updated_at, row_version)
SELECT a.id, a.id, NULLIF(a.created_by_user_id, '00000000-0000-0000-0000-000000000000'::uuid),
       'CREATED', 'ACCOUNT', '[]'::jsonb,
       a.created_by_user_id, NULL, a.created_at, a.created_at, 1
FROM crm.crm_accounts a
WHERE NOT EXISTS (
    SELECT 1 FROM crm.crm_account_change_history h
    WHERE h.account_id = a.id AND h.action_code = 'CREATED'
)
ON CONFLICT (id) DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Audit trails are deliberately retained to prevent data loss on rollback.
    }
}
