using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261113043000_AllowExtendedCrmActivityTypes")]
public sealed class AllowExtendedCrmActivityTypes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- The initial CHECK only permitted VISIT/CALL/EMAIL/MEETING/OTHER.
        -- Release 125 enabled four more Config-backed codes in the API, but not PostgreSQL.
        -- Replacing this constraint preserves all activities and their linked documents.
        ALTER TABLE crm.crm_activities
            DROP CONSTRAINT IF EXISTS ck_crm_activity_type;
        ALTER TABLE crm.crm_activities
            ADD CONSTRAINT ck_crm_activity_type
            CHECK (type IN (
                'VISIT', 'CALL', 'EMAIL', 'MEETING', 'OTHER',
                'COMMERCIAL_FOLLOWUP', 'WHATSAPP', 'RECOMMENDATION', 'OFFER'
            ));
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The old constraint would reject existing activities created with newer codes.
        // Preserve user history; never silently erase or invalidate commercial records.
    }
}
