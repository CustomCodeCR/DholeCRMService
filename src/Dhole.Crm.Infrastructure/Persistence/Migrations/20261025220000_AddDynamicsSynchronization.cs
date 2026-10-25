using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261025220000_AddDynamicsSynchronization")]
public sealed class AddDynamicsSynchronization : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_external_mappings (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL,
 external_system varchar(32) NOT NULL, entity_type varchar(32) NOT NULL,
 entity_id uuid NOT NULL, external_id varchar(128) NOT NULL,
 external_version varchar(160), last_synced_at timestamptz,
 last_payload_hash varchar(64), sync_state varchar(32) NOT NULL,
 CONSTRAINT ck_crm_ext_id_nonempty CHECK (length(trim(external_id)) > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_ext_remote ON crm.crm_external_mappings(external_system,entity_type,external_id);
CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_ext_local ON crm.crm_external_mappings(external_system,entity_type,entity_id);
CREATE TABLE IF NOT EXISTS crm.crm_sync_jobs (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL,
 source_system varchar(32) NOT NULL, direction varchar(16) NOT NULL,
 entity_type varchar(32) NOT NULL, status varchar(24) NOT NULL,
 dry_run boolean NOT NULL, cursor text, started_at timestamptz, finished_at timestamptz,
 processed_count integer NOT NULL DEFAULT 0, created_count integer NOT NULL DEFAULT 0,
 unchanged_count integer NOT NULL DEFAULT 0, conflict_count integer NOT NULL DEFAULT 0,
 error_count integer NOT NULL DEFAULT 0, attempt_count integer NOT NULL DEFAULT 0,
 next_attempt_at timestamptz, last_error_code varchar(80),
 configuration_hash varchar(64), request_key_hash varchar(64), last_operator_user_id uuid,
 CONSTRAINT ck_crm_sync_status CHECK (status IN ('Pending','Running','Completed','Failed','Paused')),
 CONSTRAINT ck_crm_sync_counts CHECK (processed_count >= 0 AND created_count >= 0
  AND unchanged_count >= 0 AND conflict_count >= 0 AND error_count >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_sync_job_key ON crm.crm_sync_jobs(request_key_hash) WHERE request_key_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_crm_sync_jobs_status ON crm.crm_sync_jobs(status,created_at);
CREATE TABLE IF NOT EXISTS crm.crm_sync_conflicts (
 id uuid PRIMARY KEY, created_by_user_id uuid NOT NULL, updated_by_user_id uuid,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL, row_version bigint NOT NULL,
 sync_job_id uuid NOT NULL REFERENCES crm.crm_sync_jobs(id) ON DELETE RESTRICT,
 entity_type varchar(32) NOT NULL, external_id varchar(128) NOT NULL,
 existing_entity_id uuid, reason_code varchar(64) NOT NULL,
 resolution varchar(24) NOT NULL DEFAULT 'Pending',
 resolved_at timestamptz, resolved_by_user_id uuid
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_crm_sync_conflict ON
 crm.crm_sync_conflicts(sync_job_id,entity_type,external_id,reason_code);
""");
    protected override void Down(MigrationBuilder b) =>
        throw new NotSupportedException("External mappings and reconciliation logs are historical and cannot be dropped automatically.");
}
