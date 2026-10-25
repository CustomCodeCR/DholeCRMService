using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261014210000_AddCommercialPipelineActivitiesAnalysis")]
public sealed class AddCommercialPipelineActivitiesAnalysis : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_sales_pipelines (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    name varchar(120) NOT NULL UNIQUE,
    is_default boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    sort_order integer NOT NULL DEFAULT 0
);

CREATE TABLE crm.crm_sales_pipeline_stages (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    pipeline_id uuid NOT NULL,
    name varchar(120) NOT NULL,
    sort_order integer NOT NULL,
    default_win_probability numeric(7,4) NOT NULL,
    is_won boolean NOT NULL DEFAULT false,
    is_lost boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT ck_crm_stage_probability CHECK (default_win_probability BETWEEN 0 AND 100),
    CONSTRAINT ck_crm_stage_terminal CHECK (NOT (is_won AND is_lost))
);

CREATE TABLE crm.crm_opportunities (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid,
    lead_id uuid,
    title varchar(250) NOT NULL,
    executive_user_id uuid NOT NULL,
    pipeline_id uuid NOT NULL,
    stage_id uuid NOT NULL,
    amount_estimated numeric(18,2) NOT NULL,
    currency_code varchar(12) NOT NULL,
    probability_pct numeric(7,4) NOT NULL,
    expected_close_at timestamptz,
    actual_close_at timestamptz,
    status varchar(30) NOT NULL DEFAULT 'Open',
    loss_reason_code varchar(64),
    description text,
    archived_at timestamptz,
    CONSTRAINT ck_crm_op_subject CHECK (num_nonnulls(account_id,lead_id) = 1),
    CONSTRAINT ck_crm_op_amount CHECK (amount_estimated >= 0),
    CONSTRAINT ck_crm_op_probability CHECK (probability_pct BETWEEN 0 AND 100),
    CONSTRAINT ck_crm_op_status CHECK (status IN ('Open','Won','Lost')),
    CONSTRAINT ck_crm_op_loss CHECK (status <> 'Lost' OR nullif(loss_reason_code, '') IS NOT NULL)
);

CREATE TABLE crm.crm_opportunity_stage_history (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    opportunity_id uuid NOT NULL,
    from_stage_id uuid,
    to_stage_id uuid NOT NULL,
    changed_by uuid NOT NULL,
    changed_at timestamptz NOT NULL,
    reason text
);

CREATE TABLE crm.crm_opportunity_negotiations (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    opportunity_id uuid NOT NULL,
    pricing_quote_id uuid,
    negotiated_by uuid NOT NULL,
    status varchar(32) NOT NULL,
    change_summary text NOT NULL,
    agreed_terms text,
    next_action_at timestamptz
);

CREATE TABLE crm.crm_activities (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid,
    lead_id uuid,
    opportunity_id uuid,
    contact_id uuid,
    owner_user_id uuid NOT NULL,
    type varchar(24) NOT NULL,
    subject varchar(250) NOT NULL,
    scheduled_start_at timestamptz,
    scheduled_end_at timestamptz,
    occurred_at timestamptz,
    status varchar(24) NOT NULL,
    outcome varchar(64),
    location_text text,
    archived_at timestamptz,
    CONSTRAINT ck_crm_activity_subject CHECK (num_nonnulls(account_id,lead_id,opportunity_id) >= 1),
    CONSTRAINT ck_crm_activity_window CHECK (scheduled_start_at IS NULL OR scheduled_end_at IS NULL OR scheduled_end_at >= scheduled_start_at),
    CONSTRAINT ck_crm_activity_type CHECK (type IN ('VISIT','CALL','EMAIL','MEETING','OTHER')),
    CONSTRAINT ck_crm_activity_status CHECK (status IN ('Scheduled','Completed','Cancelled')),
    CONSTRAINT ck_crm_activity_completion CHECK (status <> 'Completed' OR occurred_at IS NOT NULL)
);

CREATE TABLE crm.crm_activity_participants (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    activity_id uuid NOT NULL,
    contact_id uuid,
    user_id uuid,
    participant_name varchar(240),
    role varchar(60) NOT NULL,
    CONSTRAINT ck_crm_participant_subject CHECK (num_nonnulls(contact_id,user_id,participant_name) = 1)
);

CREATE TABLE crm.crm_activity_notes (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    activity_id uuid NOT NULL,
    author_user_id uuid NOT NULL,
    note_text varchar(8000) NOT NULL
);

CREATE TABLE crm.crm_commercial_analyses (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid,
    lead_id uuid,
    opportunity_id uuid,
    owner_user_id uuid NOT NULL,
    market_code varchar(64) NOT NULL,
    customs_nationalization_required boolean NOT NULL DEFAULT false,
    observations text,
    estimated_monthly_volume numeric(18,4),
    volume_unit varchar(24),
    reviewed_at timestamptz,
    CONSTRAINT ck_crm_analysis_subject CHECK (num_nonnulls(account_id,lead_id,opportunity_id) >= 1),
    CONSTRAINT ck_crm_analysis_volume CHECK (estimated_monthly_volume IS NULL OR estimated_monthly_volume >= 0)
);

CREATE TABLE crm.crm_commercial_analysis_routes (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    analysis_id uuid NOT NULL,
    pol_code varchar(80),
    poe_code varchar(80),
    pod_code varchar(80),
    origin_country_code varchar(80),
    destination_country_code varchar(80),
    frequency_per_month numeric(18,4),
    notes text,
    CONSTRAINT ck_crm_route_nonempty CHECK (num_nonnulls(pol_code,poe_code,pod_code,origin_country_code,destination_country_code) >= 1),
    CONSTRAINT ck_crm_route_frequency CHECK (frequency_per_month IS NULL OR frequency_per_month >= 0)
);

CREATE TABLE crm.crm_commercial_analysis_services (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    analysis_id uuid NOT NULL,
    service_code varchar(80) NOT NULL,
    modality_code varchar(80) NOT NULL,
    transport_mode_code varchar(80) NOT NULL,
    requires_customs boolean NOT NULL DEFAULT false,
    cargo_type_code varchar(80),
    volume numeric(18,4) NOT NULL,
    volume_unit varchar(24) NOT NULL,
    container_type_code varchar(80),
    equipment_quantity integer,
    expected_movements_per_month numeric(18,4),
    notes text,
    CONSTRAINT ck_crm_analysis_service_volume CHECK (volume >= 0),
    CONSTRAINT ck_crm_analysis_service_equipment CHECK (equipment_quantity IS NULL OR equipment_quantity > 0),
    CONSTRAINT ck_crm_analysis_service_frequency CHECK (expected_movements_per_month IS NULL OR expected_movements_per_month >= 0)
);

CREATE TABLE crm.crm_customer_trade_profiles (
    id uuid PRIMARY KEY,
    created_by_user_id uuid NOT NULL,
    updated_by_user_id uuid,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    row_version bigint NOT NULL DEFAULT 1,
    account_id uuid NOT NULL,
    service_code varchar(80) NOT NULL,
    preferred_origin_code varchar(80),
    preferred_destination_code varchar(80),
    frequency varchar(32) NOT NULL,
    expected_volume numeric(18,4) NOT NULL,
    volume_unit varchar(24) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    CONSTRAINT ck_crm_trade_expected_volume CHECK (expected_volume >= 0)
);

CREATE TABLE crm.crm_opportunity_contacts (
 opportunity_id uuid NOT NULL,
 contact_id uuid NOT NULL,
 decision_role varchar(60) NOT NULL,
 PRIMARY KEY (opportunity_id,contact_id)
);

ALTER TABLE crm.crm_sales_pipeline_stages ADD CONSTRAINT fk_crm_sales_pipeline_stages_pipeline_id FOREIGN KEY (pipeline_id) REFERENCES crm.crm_sales_pipelines(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunities ADD CONSTRAINT fk_crm_opportunities_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunities ADD CONSTRAINT fk_crm_opportunities_lead_id FOREIGN KEY (lead_id) REFERENCES crm.crm_leads(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunities ADD CONSTRAINT fk_crm_opportunities_pipeline_id FOREIGN KEY (pipeline_id) REFERENCES crm.crm_sales_pipelines(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_stage_history ADD CONSTRAINT fk_crm_opportunity_stage_history_opportunity_id FOREIGN KEY (opportunity_id) REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_stage_history ADD CONSTRAINT fk_crm_opportunity_stage_history_from_stage_id FOREIGN KEY (from_stage_id) REFERENCES crm.crm_sales_pipeline_stages(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_stage_history ADD CONSTRAINT fk_crm_opportunity_stage_history_to_stage_id FOREIGN KEY (to_stage_id) REFERENCES crm.crm_sales_pipeline_stages(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_contacts ADD CONSTRAINT fk_crm_opportunity_contacts_opportunity_id FOREIGN KEY (opportunity_id) REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_contacts ADD CONSTRAINT fk_crm_opportunity_contacts_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_opportunity_negotiations ADD CONSTRAINT fk_crm_opportunity_negotiations_opportunity_id FOREIGN KEY (opportunity_id) REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activities ADD CONSTRAINT fk_crm_activities_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activities ADD CONSTRAINT fk_crm_activities_lead_id FOREIGN KEY (lead_id) REFERENCES crm.crm_leads(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activities ADD CONSTRAINT fk_crm_activities_opportunity_id FOREIGN KEY (opportunity_id) REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activities ADD CONSTRAINT fk_crm_activities_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activity_participants ADD CONSTRAINT fk_crm_activity_participants_activity_id FOREIGN KEY (activity_id) REFERENCES crm.crm_activities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activity_participants ADD CONSTRAINT fk_crm_activity_participants_contact_id FOREIGN KEY (contact_id) REFERENCES crm.crm_contacts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_activity_notes ADD CONSTRAINT fk_crm_activity_notes_activity_id FOREIGN KEY (activity_id) REFERENCES crm.crm_activities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_commercial_analyses ADD CONSTRAINT fk_crm_commercial_analyses_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_commercial_analyses ADD CONSTRAINT fk_crm_commercial_analyses_lead_id FOREIGN KEY (lead_id) REFERENCES crm.crm_leads(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_commercial_analyses ADD CONSTRAINT fk_crm_commercial_analyses_opportunity_id FOREIGN KEY (opportunity_id) REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_commercial_analysis_routes ADD CONSTRAINT fk_crm_commercial_analysis_routes_analysis_id FOREIGN KEY (analysis_id) REFERENCES crm.crm_commercial_analyses(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_commercial_analysis_services ADD CONSTRAINT fk_crm_commercial_analysis_services_analysis_id FOREIGN KEY (analysis_id) REFERENCES crm.crm_commercial_analyses(id) ON DELETE RESTRICT;

ALTER TABLE crm.crm_customer_trade_profiles ADD CONSTRAINT fk_crm_customer_trade_profiles_account_id FOREIGN KEY (account_id) REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX uq_crm_default_pipeline ON crm.crm_sales_pipelines(is_default) WHERE is_default AND is_active;

CREATE UNIQUE INDEX uq_crm_stage_pipeline_sort ON crm.crm_sales_pipeline_stages(pipeline_id,sort_order);

CREATE UNIQUE INDEX uq_crm_stage_pipeline_id ON crm.crm_sales_pipeline_stages(pipeline_id,id);

CREATE INDEX ix_crm_opportunity_exec_status_due ON crm.crm_opportunities(executive_user_id,status,expected_close_at);

CREATE INDEX ix_crm_opportunity_account ON crm.crm_opportunities(account_id);

CREATE INDEX ix_crm_opportunity_lead ON crm.crm_opportunities(lead_id);

CREATE INDEX ix_crm_opportunity_stage_changes ON crm.crm_opportunity_stage_history(opportunity_id,changed_at DESC);

CREATE INDEX ix_crm_opportunity_negotiation_history ON crm.crm_opportunity_negotiations(opportunity_id,created_at DESC);

CREATE INDEX ix_crm_activity_timeline ON crm.crm_activities(account_id,occurred_at DESC);

CREATE INDEX ix_crm_activity_scheduled ON crm.crm_activities(owner_user_id,status,scheduled_start_at);

CREATE INDEX ix_crm_activity_participants ON crm.crm_activity_participants(activity_id);

CREATE INDEX ix_crm_activity_notes ON crm.crm_activity_notes(activity_id,created_at DESC);

CREATE INDEX ix_crm_analysis_owner ON crm.crm_commercial_analyses(owner_user_id,created_at DESC);

CREATE INDEX ix_crm_analysis_routes ON crm.crm_commercial_analysis_routes(analysis_id);

CREATE INDEX ix_crm_analysis_services ON crm.crm_commercial_analysis_services(analysis_id);

CREATE INDEX ix_crm_trade_profile_account ON crm.crm_customer_trade_profiles(account_id,service_code);

ALTER TABLE crm.crm_opportunities ADD CONSTRAINT fk_crm_opportunity_pipeline_stage FOREIGN KEY (pipeline_id,stage_id) REFERENCES crm.crm_sales_pipeline_stages(pipeline_id,id) ON DELETE RESTRICT;

INSERT INTO crm.crm_sales_pipelines (id,created_by_user_id,created_at,updated_at,name,is_default,is_active,sort_order)
VALUES ('c3000000-0000-4000-8000-000000000001','00000000-0000-0000-0000-000000000000',now(),now(),'Comercial',true,true,10) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000001','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Calificación',10,15,false,false,true) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000002','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Oportunidad',20,30,false,false,true) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000003','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Propuesta',30,50,false,false,true) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000004','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Negociación',40,75,false,false,true) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000005','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Ganada',50,100,true,false,true) ON CONFLICT (id) DO NOTHING;

INSERT INTO crm.crm_sales_pipeline_stages (id,created_by_user_id,created_at,updated_at,pipeline_id,name,sort_order,default_win_probability,is_won,is_lost,is_active)
VALUES ('c3100000-0000-4000-8000-000000000006','00000000-0000-0000-0000-000000000000',now(),now(),'c3000000-0000-4000-8000-000000000001','Perdida',60,0,false,true,true) ON CONFLICT (id) DO NOTHING;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Never remove commercial history automatically; use a reviewed restore procedure.");
    }
}
