using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261029180500_AddAccountBusinessProfiles")]
public sealed class AddAccountBusinessProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_account_business_profiles (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL UNIQUE REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    phone varchar(60),
    email varchar(254),
    website varchar(500),
    sector varchar(350),
    business_activity varchar(2000),
    products varchar(2000),
    company_history varchar(4000),
    billing_email varchar(254),
    marketing_email varchar(254),
    commercial_observations varchar(4000),
    operational_instructions varchar(4000)
);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS crm.crm_account_business_profiles;");
    }
}
