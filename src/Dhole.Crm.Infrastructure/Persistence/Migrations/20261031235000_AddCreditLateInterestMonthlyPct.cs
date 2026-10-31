using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Dhole.Crm.Infrastructure.Persistence;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261031235000_AddCreditLateInterestMonthlyPct")]
public sealed class AddCreditLateInterestMonthlyPct : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_credit_profiles
    ADD COLUMN IF NOT EXISTS late_interest_monthly_pct numeric(7,4) NOT NULL DEFAULT 0;
ALTER TABLE crm.crm_credit_profiles
    ADD CONSTRAINT ck_credit_late_interest_monthly_pct
    CHECK (late_interest_monthly_pct BETWEEN 0 AND 100);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Removing a financial term from credit records is not a safe automatic rollback.");
    }
}
