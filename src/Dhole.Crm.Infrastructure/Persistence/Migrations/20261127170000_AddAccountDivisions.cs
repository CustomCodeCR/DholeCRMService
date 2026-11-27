using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261127170000_AddAccountDivisions")]
public sealed class AddAccountDivisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_account_divisions (
    id uuid NOT NULL PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    name varchar(120) NOT NULL,
    normalized_name varchar(120) NOT NULL,
    UNIQUE (account_id, id),
    UNIQUE (account_id, normalized_name)
);
CREATE TABLE crm.crm_account_division_contacts (
    account_id uuid NOT NULL,
    division_id uuid NOT NULL,
    contact_id uuid NOT NULL,
    PRIMARY KEY (account_id, division_id, contact_id),
    FOREIGN KEY (account_id, division_id) REFERENCES crm.crm_account_divisions(account_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (account_id, contact_id) REFERENCES crm.crm_account_contacts(account_id, contact_id) ON DELETE RESTRICT
);
CREATE INDEX ix_crm_account_division_contacts_account_id_contact_id
    ON crm.crm_account_division_contacts(account_id, contact_id);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve division identity and membership during an application rollback.
    }
}
