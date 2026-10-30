using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261030221000_AddAccountViewerGrants")]
public sealed class AddAccountViewerGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_account_viewer_grants (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    user_id uuid NOT NULL,
    granted_by_user_id uuid NOT NULL,
    revoked_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_account_viewer_grants_active
    ON crm.crm_account_viewer_grants (account_id, user_id) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_crm_account_viewer_grants_user
    ON crm.crm_account_viewer_grants (user_id);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS crm.crm_account_viewer_grants;");
    }
}
