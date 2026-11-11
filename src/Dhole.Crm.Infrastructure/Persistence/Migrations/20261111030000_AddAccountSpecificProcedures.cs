using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111030000_AddAccountSpecificProcedures")]
public sealed class AddAccountSpecificProcedures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_account_specific_procedures (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    name varchar(160) NOT NULL,
    detail varchar(8000),
    assigned_team varchar(500),
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_crm_specific_procedures_name_not_empty CHECK (length(trim(name)) > 0)
);
CREATE INDEX IF NOT EXISTS ix_crm_account_specific_procedures_account_id_updated_at
    ON crm.crm_account_specific_procedures (account_id, updated_at);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately retain customer procedures on rollback; no destructive rollback.
    }
}
