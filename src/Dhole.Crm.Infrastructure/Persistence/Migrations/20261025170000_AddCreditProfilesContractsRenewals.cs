using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261025170000_AddCreditProfilesContractsRenewals")]
public sealed class AddCreditProfilesContractsRenewals : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
CREATE TABLE crm.crm_credit_profiles (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL DEFAULT 1,
 account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
 credit_enabled boolean NOT NULL DEFAULT false, credit_days int NOT NULL,
 credit_limit numeric(18,2) NOT NULL, currency_code varchar(12) NOT NULL,
 monthly_financing_pct numeric(7,4) NOT NULL, payment_conditions varchar(4000) NOT NULL,
 status varchar(24) NOT NULL DEFAULT 'PendingApproval', approved_by_user_id uuid,
 approved_at timestamptz, effective_from timestamptz NOT NULL, review_at timestamptz,
 CONSTRAINT ck_credit_numbers CHECK (credit_days BETWEEN 0 AND 3650 AND credit_limit>=0
 AND monthly_financing_pct BETWEEN 0 AND 100),
 CONSTRAINT ck_credit_status CHECK (status IN ('PendingApproval','Approved','Rejected','Superseded')),
 CONSTRAINT ck_credit_approval CHECK (status <> 'Approved' OR (approved_by_user_id IS NOT NULL AND approved_at IS NOT NULL)),
 CONSTRAINT ck_credit_currency CHECK (length(trim(currency_code)) BETWEEN 3 AND 12)
);
CREATE UNIQUE INDEX uq_credit_approved ON crm.crm_credit_profiles(account_id) WHERE status='Approved';
CREATE UNIQUE INDEX uq_credit_pending ON crm.crm_credit_profiles(account_id) WHERE status='PendingApproval';
CREATE INDEX ix_credit_review ON crm.crm_credit_profiles(status,review_at);
CREATE TABLE crm.crm_credit_coverages (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL DEFAULT 1,
 credit_profile_id uuid NOT NULL REFERENCES crm.crm_credit_profiles(id) ON DELETE RESTRICT,
 service_code varchar(80) NOT NULL, coverage_type varchar(12) NOT NULL,
 description varchar(2000) NOT NULL, is_active boolean NOT NULL DEFAULT true,
 CONSTRAINT ck_credit_coverage_type CHECK (coverage_type IN ('INCLUDED','EXCLUDED'))
);
CREATE UNIQUE INDEX uq_credit_coverage_service ON crm.crm_credit_coverages(credit_profile_id,service_code) WHERE is_active;
CREATE TABLE crm.crm_credit_contracts (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL DEFAULT 1,
 account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
 credit_profile_id uuid REFERENCES crm.crm_credit_profiles(id) ON DELETE RESTRICT,
 contract_number varchar(100), start_date timestamptz NOT NULL,
 renewal_date timestamptz NOT NULL, expiry_date timestamptz,
 payment_terms varchar(4000) NOT NULL, status varchar(20) NOT NULL,
 auto_renew boolean NOT NULL DEFAULT false, signed_document_file_id uuid,
 CONSTRAINT ck_contract_dates CHECK (renewal_date>=start_date AND (expiry_date IS NULL OR expiry_date>=start_date)),
 CONSTRAINT ck_contract_status CHECK (status IN ('Draft','Active','Expired','Cancelled'))
);
CREATE UNIQUE INDEX uq_contract_number ON crm.crm_credit_contracts(contract_number) WHERE contract_number IS NOT NULL;
CREATE INDEX ix_contract_renewal ON crm.crm_credit_contracts(status,renewal_date);
CREATE TABLE crm.crm_credit_terms_history (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL DEFAULT 1,
 credit_profile_id uuid NOT NULL REFERENCES crm.crm_credit_profiles(id) ON DELETE RESTRICT,
 changed_by uuid NOT NULL, changed_at timestamptz NOT NULL,
 old_values_json text NOT NULL, new_values_json text NOT NULL,
 approval_reason varchar(1000) NOT NULL, effective_at timestamptz NOT NULL
);
CREATE INDEX ix_credit_terms_history ON crm.crm_credit_terms_history(credit_profile_id,changed_at);
CREATE TABLE crm.crm_credit_contract_renewals (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL DEFAULT 1,
 contract_id uuid NOT NULL REFERENCES crm.crm_credit_contracts(id) ON DELETE RESTRICT,
 operation_id uuid,
 changed_by uuid NOT NULL, previous_renewal_date timestamptz NOT NULL,
 new_renewal_date timestamptz NOT NULL, previous_expiry_date timestamptz,
 new_expiry_date timestamptz, reason varchar(1000) NOT NULL
);
CREATE UNIQUE INDEX uq_contract_renewal_operation ON crm.crm_credit_contract_renewals(operation_id) WHERE operation_id IS NOT NULL;
CREATE INDEX ix_contract_renewal_history ON crm.crm_credit_contract_renewals(contract_id,created_at);
ALTER TABLE crm.crm_reminders DROP CONSTRAINT ck_crm_reminder_entity;
ALTER TABLE crm.crm_reminders ADD CONSTRAINT ck_crm_reminder_entity
 CHECK (entity_type IN ('Followup','Activity','CreditProfile','CreditContract'));
""");
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Financial records and renewal history cannot be deleted by automatic rollback.");
}
