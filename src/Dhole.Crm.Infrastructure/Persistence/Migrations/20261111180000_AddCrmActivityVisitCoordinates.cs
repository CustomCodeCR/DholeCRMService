using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CrmDbContext))]
[Migration("20261111180000_AddCrmActivityVisitCoordinates")]
public sealed class AddCrmActivityVisitCoordinates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_activities
  ADD COLUMN IF NOT EXISTS location_latitude numeric(9,6),
  ADD COLUMN IF NOT EXISTS location_longitude numeric(9,6);
""");
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Retain user-entered locations on rollback.
    }
}
