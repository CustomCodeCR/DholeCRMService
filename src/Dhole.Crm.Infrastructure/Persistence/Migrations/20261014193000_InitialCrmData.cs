using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261014193000_InitialCrmData")]
public sealed class InitialCrmData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE SCHEMA IF NOT EXISTS crm;

CREATE TABLE IF NOT EXISTS crm.crm_accounts (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    display_name varchar(220) NOT NULL,
    legal_name varchar(220) NOT NULL,
    country_id uuid,
    tax_id_type_code varchar(64),
    tax_id varchar(80),
    normalized_tax_id varchar(80),
    customer_type_code varchar(10) NOT NULL DEFAULT 'cash',
    source_code varchar(64),
    relationship_type_code varchar(64),
    category_type varchar(12) NOT NULL,
    category_code varchar(1) NOT NULL,
    executive_user_id uuid NOT NULL,
    verification_status varchar(32) NOT NULL DEFAULT 'Pending',
    hacienda_verified_at timestamptz,
    status varchar(32) NOT NULL DEFAULT 'Active',
    archived_at timestamptz,
    CONSTRAINT ck_account_category CHECK ((category_type='CUSTOMER' AND category_code IN ('A','B','C','D')) OR (category_type='NVOCC' AND category_code IN ('A','B'))),
    CONSTRAINT ck_customer_type CHECK (customer_type_code IN ('cash','credit'))
);

CREATE TABLE IF NOT EXISTS crm.crm_leads (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    name varchar(220) NOT NULL,
    company_name varchar(220),
    country_id uuid,
    tax_id varchar(80),
    source_code varchar(64),
    relationship_type_code varchar(64),
    executive_user_id uuid NOT NULL,
    qualification_status varchar(64) NOT NULL DEFAULT 'New',
    contact_email varchar(254),
    phone varchar(60),
    converted_at timestamptz,
    converted_account_id uuid,
    archived_at timestamptz
);

CREATE TABLE IF NOT EXISTS crm.crm_lead_conversions (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    lead_id uuid NOT NULL,
    account_id uuid NOT NULL,
    converted_by uuid NOT NULL,
    converted_at timestamptz NOT NULL,
    notes text
);

CREATE TABLE IF NOT EXISTS crm.crm_account_assignments (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL,
    executive_user_id uuid NOT NULL,
    effective_from timestamptz NOT NULL,
    effective_to timestamptz,
    assigned_by uuid NOT NULL,
    reason text
);

CREATE TABLE IF NOT EXISTS crm.crm_contacts (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    name varchar(220) NOT NULL,
    job_title varchar(200),
    country_id uuid,
    phone varchar(60),
    email varchar(254),
    status varchar(32) NOT NULL DEFAULT 'Active',
    archived_at timestamptz
);

CREATE TABLE IF NOT EXISTS crm.crm_addresses (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL,
    contact_id uuid,
    country_id uuid,
    address_type varchar(40) NOT NULL,
    address_text text NOT NULL,
    location text
);

CREATE TABLE IF NOT EXISTS crm.crm_contact_preferences (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    contact_id uuid NOT NULL,
    preferred_channel varchar(30) NOT NULL,
    preferred_days_mask integer NOT NULL,
    start_local_time time,
    end_local_time time,
    timezone_id varchar(128) NOT NULL,
    frequency_days integer,
    do_not_contact boolean NOT NULL DEFAULT false,
    CONSTRAINT ck_preferred_days_mask CHECK (preferred_days_mask BETWEEN 0 AND 127),
    CONSTRAINT ck_frequency_days CHECK (frequency_days IS NULL OR frequency_days > 0),
    CONSTRAINT ck_contact_time_window CHECK (start_local_time IS NULL OR end_local_time IS NULL OR start_local_time < end_local_time)
);

CREATE TABLE IF NOT EXISTS crm.crm_marketing_consents (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    contact_id uuid NOT NULL,
    account_id uuid,
    channel varchar(32) NOT NULL,
    purpose varchar(64) NOT NULL,
    status varchar(32) NOT NULL,
    source varchar(64) NOT NULL,
    captured_at timestamptz NOT NULL,
    revoked_at timestamptz,
    evidence_reference text,
    CONSTRAINT ck_consent_status CHECK (status IN ('UNKNOWN','OPTED_IN','OPTED_OUT','REVOKED'))
);

CREATE TABLE IF NOT EXISTS crm.crm_account_business_activities (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL,
    activity_code varchar(64) NOT NULL,
    activity_description text,
    source varchar(64) NOT NULL,
    hacienda_snapshot_at timestamptz
);

CREATE TABLE IF NOT EXISTS crm.crm_account_contacts (
 account_id uuid NOT NULL,
 contact_id uuid NOT NULL,
 relation_role varchar(40) NOT NULL DEFAULT 'OTHER',
 is_primary boolean NOT NULL DEFAULT false,
 active_from timestamptz NOT NULL DEFAULT now(),
 active_to timestamptz,
 PRIMARY KEY (account_id, contact_id)
);

ALTER TABLE crm.crm_leads ADD CONSTRAINT fk_crm_leads_converted_account_id FOREIGN KEY (converted_account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_lead_conversions ADD CONSTRAINT fk_crm_lead_conversions_lead_id FOREIGN KEY (lead_id) REFERENCES crm.crm_leads(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_lead_conversions ADD CONSTRAINT fk_crm_lead_conversions_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_account_assignments ADD CONSTRAINT fk_crm_account_assignments_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_account_contacts ADD CONSTRAINT fk_crm_account_contacts_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_account_contacts ADD CONSTRAINT fk_crm_account_contacts_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_addresses ADD CONSTRAINT fk_crm_addresses_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_addresses ADD CONSTRAINT fk_crm_addresses_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_contact_preferences ADD CONSTRAINT fk_crm_contact_preferences_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_marketing_consents ADD CONSTRAINT fk_crm_marketing_consents_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_marketing_consents ADD CONSTRAINT fk_crm_marketing_consents_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_account_business_activities ADD CONSTRAINT fk_crm_account_business_activities_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_account_country_tax ON crm.crm_accounts (country_id, tax_id_type_code, normalized_tax_id) WHERE normalized_tax_id IS NOT NULL AND archived_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_crm_accounts_executive ON crm.crm_accounts (executive_user_id) ;

CREATE INDEX IF NOT EXISTS ix_crm_accounts_name ON crm.crm_accounts (display_name) ;

CREATE INDEX IF NOT EXISTS ix_crm_leads_executive ON crm.crm_leads (executive_user_id) ;

CREATE INDEX IF NOT EXISTS ix_crm_leads_account ON crm.crm_leads (converted_account_id) ;

CREATE INDEX IF NOT EXISTS ix_crm_leads_name ON crm.crm_leads (name) ;

CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_lead_conversions_lead ON crm.crm_lead_conversions (lead_id) ;

CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_assignment_active ON crm.crm_account_assignments (account_id) WHERE effective_to IS NULL;

CREATE INDEX IF NOT EXISTS ix_crm_account_contacts_contact ON crm.crm_account_contacts (contact_id) ;

CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_account_primary_contact ON crm.crm_account_contacts (account_id) WHERE is_primary AND active_to IS NULL;

CREATE INDEX IF NOT EXISTS ix_crm_contacts_email ON crm.crm_contacts (email) ;

CREATE UNIQUE INDEX IF NOT EXISTS ix_crm_contact_preferences_contact ON crm.crm_contact_preferences (contact_id) ;

CREATE INDEX IF NOT EXISTS ix_crm_marketing_consent_latest ON crm.crm_marketing_consents (contact_id, channel, purpose, captured_at DESC) ;

CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_activity_account_code ON crm.crm_account_business_activities (account_id, activity_code) ;

CREATE INDEX IF NOT EXISTS ix_crm_addresses_account ON crm.crm_addresses (account_id) ;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("CRM data preservation policy prohibits dropping CRM tables automatically.");
    }
}
