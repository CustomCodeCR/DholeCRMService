using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261014220000_AddPricingCommercialLinks")]
public sealed class AddPricingCommercialLinks : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
CREATE TABLE crm.crm_opportunity_pricing_requests (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL,
  row_version bigint NOT NULL DEFAULT 1,
  opportunity_id uuid NOT NULL REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT,
  pricing_request_id uuid NOT NULL,
  seller_user_id uuid NOT NULL,
  pricing_quote_id uuid,
  status_snapshot varchar(50) NOT NULL,
  idempotency_key_hash varchar(64),
  linked_at timestamptz NOT NULL,
  last_sync_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX uq_crm_pricing_request ON crm.crm_opportunity_pricing_requests(pricing_request_id);
CREATE UNIQUE INDEX uq_crm_opportunity_pricing_request ON crm.crm_opportunity_pricing_requests(opportunity_id,pricing_request_id);
CREATE UNIQUE INDEX uq_crm_pricing_request_idempotency ON crm.crm_opportunity_pricing_requests(opportunity_id,idempotency_key_hash)
 WHERE idempotency_key_hash IS NOT NULL;
CREATE TABLE crm.crm_opportunity_quotes (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL,
  row_version bigint NOT NULL DEFAULT 1,
  opportunity_id uuid NOT NULL REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT,
  pricing_quote_id uuid NOT NULL,
  pricing_request_id uuid,
  quote_number_snapshot varchar(100) NOT NULL,
  commercial_status_snapshot varchar(50) NOT NULL,
  shipment_mode_snapshot varchar(60) NOT NULL,
  linked_at timestamptz NOT NULL,
  last_sync_at timestamptz NOT NULL,
  CONSTRAINT ck_crm_quote_status CHECK (commercial_status_snapshot IN
    ('PendingApproval','ApprovedByManagement','RejectedByManagement','Open','Sent',
     'AcceptedByClient','RejectedByClient','RequestedByClient','Closed','Expired','Cancelled'))
);
CREATE UNIQUE INDEX uq_crm_opportunity_quote ON crm.crm_opportunity_quotes(opportunity_id,pricing_quote_id);
CREATE INDEX ix_crm_opportunity_quote_request ON crm.crm_opportunity_quotes(pricing_request_id);
""");
    }
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Pricing CRM links and commercial history must not be destroyed automatically.");
}
