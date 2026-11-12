using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261112091000_AddAccountGrouping")]
public sealed class AddAccountGrouping : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE crm.crm_accounts ADD COLUMN IF NOT EXISTS parent_account_id uuid NULL;
        CREATE INDEX IF NOT EXISTS ix_crm_accounts_parent_account_id
            ON crm.crm_accounts (parent_account_id);
        ALTER TABLE crm.crm_accounts
            ADD CONSTRAINT fk_crm_accounts_parent_account_id
            FOREIGN KEY (parent_account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;
        ALTER TABLE crm.crm_accounts
            ADD CONSTRAINT ck_crm_accounts_no_self_parent
            CHECK (parent_account_id IS NULL OR parent_account_id <> id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE crm.crm_accounts DROP CONSTRAINT IF EXISTS ck_crm_accounts_no_self_parent;
        ALTER TABLE crm.crm_accounts DROP CONSTRAINT IF EXISTS fk_crm_accounts_parent_account_id;
        DROP INDEX IF EXISTS crm.ix_crm_accounts_parent_account_id;
        ALTER TABLE crm.crm_accounts DROP COLUMN IF EXISTS parent_account_id;
        """);
}
