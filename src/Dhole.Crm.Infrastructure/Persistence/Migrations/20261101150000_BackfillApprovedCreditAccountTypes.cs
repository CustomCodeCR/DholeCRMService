using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261101150000_BackfillApprovedCreditAccountTypes")]
public sealed class BackfillApprovedCreditAccountTypes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve existing accounts and approved financial records.
        migrationBuilder.Sql("""
UPDATE crm.crm_accounts AS account
SET customer_type_code = 'credit',
    updated_at = now(),
    row_version = account.row_version + 1
WHERE account.archived_at IS NULL
  AND account.customer_type_code <> 'credit'
  AND EXISTS (
      SELECT 1 FROM crm.crm_credit_profiles AS credit
      WHERE credit.account_id = account.id AND credit.status = 'Approved'
  );
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not undo customer credit classifications: another approval
        // or an operator action may have occurred after this migration.
    }
}
