using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261111160000_ExpandCrmActivityNotesForFullEditing")]
public sealed class ExpandCrmActivityNotesForFullEditing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing minutes were limited to varchar(8000), while activity creation
        // already accepts up to 20,000 characters. TEXT is lossless and compatible.
        migrationBuilder.Sql("ALTER TABLE crm.crm_activity_notes ALTER COLUMN note_text TYPE text;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally reject rollback if a new note exceeds the historical limit.
        // Never truncate meeting minutes or agreed terms.
        migrationBuilder.Sql("ALTER TABLE crm.crm_activity_notes ALTER COLUMN note_text TYPE varchar(8000);");
    }
}
