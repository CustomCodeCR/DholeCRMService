using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261118235000_AddCrmLeadCaptureAndContacts")]
public sealed class AddCrmLeadCaptureAndContacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE crm.crm_leads ADD COLUMN IF NOT EXISTS source_detail character varying(500);
ALTER TABLE crm.crm_leads ADD COLUMN IF NOT EXISTS initial_notes character varying(5000);
CREATE TABLE IF NOT EXISTS crm.crm_lead_contacts (
    lead_id uuid NOT NULL REFERENCES crm.crm_leads(id) ON DELETE RESTRICT,
    contact_id uuid NOT NULL REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT,
    relation_role character varying(60) NOT NULL DEFAULT 'CONTACT',
    is_primary boolean NOT NULL DEFAULT false,
    is_operations_contact boolean NOT NULL DEFAULT false,
    active_from timestamp with time zone NOT NULL DEFAULT now(),
    active_to timestamp with time zone,
    PRIMARY KEY (lead_id, contact_id)
);
CREATE INDEX IF NOT EXISTS ix_crm_lead_contacts_contact_id ON crm.crm_lead_contacts(contact_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_crm_lead_contacts_lead_id ON crm.crm_lead_contacts(lead_id)
    WHERE is_primary = true AND active_to IS NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Contact and capture history is retained across rollbacks.
    }
}
