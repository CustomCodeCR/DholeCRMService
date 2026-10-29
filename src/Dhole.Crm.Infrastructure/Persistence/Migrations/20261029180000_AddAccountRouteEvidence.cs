using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261029180000_AddAccountRouteEvidence")]
public sealed class AddAccountRouteEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_account_route_evidence (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
    source_kind varchar(16) NOT NULL,
    source_id uuid NOT NULL,
    pol_code varchar(80) NOT NULL,
    poe_code varchar(80),
    pod_code varchar(80) NOT NULL,
    service_code varchar(80),
    notes varchar(2000),
    observed_at timestamptz NOT NULL,
    CONSTRAINT ck_account_route_source CHECK (source_kind IN ('MEETING', 'QUOTE')),
    CONSTRAINT ck_account_route_ports CHECK (length(trim(pol_code)) > 0 AND length(trim(pod_code)) > 0)
);
CREATE UNIQUE INDEX ux_account_route_evidence_source
    ON crm.crm_account_route_evidence (account_id, source_kind, source_id, pol_code, pod_code, coalesce(poe_code, ''));
CREATE INDEX ix_account_route_evidence_history
    ON crm.crm_account_route_evidence (account_id, observed_at DESC);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS crm.crm_account_route_evidence;");
    }
}
