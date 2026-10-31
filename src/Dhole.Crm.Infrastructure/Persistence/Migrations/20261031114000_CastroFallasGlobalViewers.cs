using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261031114000_CastroFallasGlobalViewers")]
public sealed class CastroFallasGlobalViewers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_castro_fallas_viewer_grants (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    user_id uuid NOT NULL,
    granted_by_user_id uuid NOT NULL,
    revoked_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_cf_global_viewer_active
    ON crm.crm_castro_fallas_viewer_grants(user_id) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_cf_global_viewer_user
    ON crm.crm_castro_fallas_viewer_grants(user_id);
""");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve access-control audit records during rollback.
    }
}
