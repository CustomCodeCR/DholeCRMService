using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261101214000_BackfillConvertedProspectHistory")]
public sealed class BackfillConvertedProspectHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Already converted prospects also inherit their agenda, commercial
        // analyses and opportunities. Preserve lead_id for audit/provenance.
        // Account IDs originate exclusively from explicit CRM conversions.
        migrationBuilder.Sql("""
UPDATE crm.crm_activities row
   SET account_id = lead.converted_account_id
  FROM crm.crm_leads lead
 WHERE row.lead_id = lead.id
   AND row.account_id IS NULL
   AND lead.converted_account_id IS NOT NULL
   AND lead.converted_at IS NOT NULL;
UPDATE crm.crm_followups row
   SET account_id = lead.converted_account_id
  FROM crm.crm_leads lead
 WHERE row.lead_id = lead.id
   AND row.account_id IS NULL
   AND lead.converted_account_id IS NOT NULL
   AND lead.converted_at IS NOT NULL;
UPDATE crm.crm_opportunities row
   SET account_id = lead.converted_account_id
  FROM crm.crm_leads lead
 WHERE row.lead_id = lead.id
   AND row.account_id IS NULL
   AND lead.converted_account_id IS NOT NULL
   AND lead.converted_at IS NOT NULL;
UPDATE crm.crm_commercial_analyses row
   SET account_id = lead.converted_account_id
  FROM crm.crm_leads lead
 WHERE row.lead_id = lead.id
   AND row.account_id IS NULL
   AND lead.converted_account_id IS NOT NULL
   AND lead.converted_at IS NOT NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Historical ownership must not be destructively reversed.
    }
}
