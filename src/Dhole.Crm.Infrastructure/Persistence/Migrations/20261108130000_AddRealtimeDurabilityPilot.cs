using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Additive F2 pilot. The existing CRM reminder outbox/inbox remains unchanged.
/// No API or worker uses these tables unless the realtime release flag is on.
/// </summary>
[DbContext(typeof(CrmDbContext))]
[Migration("20261108130000_AddRealtimeDurabilityPilot")]
public sealed class AddRealtimeDurabilityPilot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS crm.crm_realtime_outbox (
  event_id uuid PRIMARY KEY,
  stream_name varchar(120) NOT NULL,
  envelope_json jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  status varchar(16) NOT NULL DEFAULT 'Pending',
  attempt_count integer NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  lease_token uuid,
  lease_until timestamptz,
  published_at timestamptz,
  stream_id varchar(80),
  last_error varchar(512),
  replay_count integer NOT NULL DEFAULT 0,
  last_replay_at timestamptz,
  replay_approval_id varchar(120),
  CONSTRAINT ck_crm_realtime_replay_count CHECK (replay_count >= 0),
  CONSTRAINT ck_crm_realtime_outbox_status
    CHECK (status IN ('Pending','Leased','Published','DeadLetter')),
  CONSTRAINT ck_crm_realtime_attempt_count CHECK (attempt_count >= 0),
  CONSTRAINT ck_crm_realtime_lease
    CHECK (status <> 'Leased' OR (lease_token IS NOT NULL AND lease_until IS NOT NULL)),
  CONSTRAINT ck_crm_realtime_envelope_shape
    CHECK (jsonb_typeof(envelope_json) = 'object'),
  CONSTRAINT ck_crm_realtime_stream_name CHECK (length(trim(stream_name)) > 0)
);
CREATE INDEX IF NOT EXISTS ix_crm_realtime_outbox_pending
  ON crm.crm_realtime_outbox (next_attempt_at, created_at)
  WHERE status = 'Pending';
CREATE INDEX IF NOT EXISTS ix_crm_realtime_outbox_leases
  ON crm.crm_realtime_outbox (lease_until)
  WHERE status = 'Leased';
CREATE INDEX IF NOT EXISTS ix_crm_realtime_outbox_dlq
  ON crm.crm_realtime_outbox (created_at)
  WHERE status = 'DeadLetter';

CREATE TABLE IF NOT EXISTS crm.crm_realtime_inbox (
  event_id uuid NOT NULL,
  consumer_name varchar(120) NOT NULL,
  processed_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (event_id, consumer_name)
);
CREATE INDEX IF NOT EXISTS ix_crm_realtime_inbox_processed_at
  ON crm.crm_realtime_inbox (processed_at);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Realtime outbox/inbox delivery history must not be destroyed automatically. " +
            "Disable the feature flag and restore the previous application release; " +
            "data/schema rollback requires a reviewed retention and backup procedure.");
}
