using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CrmDbContext))]
[Migration("20261111190000_AddCrmLeadAddresses")]
public sealed class AddCrmLeadAddresses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_lead_addresses (
    id uuid PRIMARY KEY,
    lead_id uuid NOT NULL REFERENCES crm.crm_leads (id) ON DELETE RESTRICT,
    country_id uuid NULL,
    address_type varchar(40) NOT NULL,
    address_text text NOT NULL,
    location text NULL,
    country_code varchar(2) NULL,
    admin1_code varchar(160) NULL, admin1_name varchar(160) NULL,
    admin2_code varchar(160) NULL, admin2_name varchar(160) NULL,
    admin3_code varchar(160) NULL, admin3_name varchar(160) NULL,
    postal_code varchar(32) NULL,
    location_latitude numeric(9,6) NULL,
    location_longitude numeric(9,6) NULL,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    row_version bigint NOT NULL,
    CONSTRAINT ck_crm_lead_addresses_coordinates CHECK (
        (location_latitude IS NULL AND location_longitude IS NULL) OR
        (location_latitude BETWEEN -90 AND 90 AND location_longitude BETWEEN -180 AND 180))
);
CREATE INDEX IF NOT EXISTS ix_crm_lead_addresses_lead_id
    ON crm.crm_lead_addresses (lead_id);
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve registered prospect locations when rolling back application code.
    }
}
