using Dhole.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CrmDbContext))]
[Migration("20261025150000_AddFollowupsRemindersMessaging")]
public sealed class AddFollowupsRemindersMessaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE crm.crm_account_followup_policies (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1,
  account_id uuid NOT NULL REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
  owner_user_id uuid NOT NULL,
  enabled boolean NOT NULL DEFAULT true,
  channel_strategy varchar(12) NOT NULL,
  frequency_days integer NOT NULL,
  next_due_at timestamptz NOT NULL,
  timezone_id varchar(128) NOT NULL,
  reminder_lead_minutes integer NOT NULL,
  paused_until timestamptz,
  CONSTRAINT ck_crm_policy_frequency CHECK (frequency_days BETWEEN 1 AND 365),
  CONSTRAINT ck_crm_policy_reminder CHECK (reminder_lead_minutes BETWEEN 0 AND 10080),
  CONSTRAINT ck_crm_policy_channel CHECK (channel_strategy IN ('BOTH','EMAIL','SYSTEM'))
);
CREATE UNIQUE INDEX uq_crm_followup_policy_account ON crm.crm_account_followup_policies(account_id);
CREATE INDEX ix_crm_followup_policy_due ON crm.crm_account_followup_policies(enabled,next_due_at);

CREATE TABLE crm.crm_followups (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1,
  account_id uuid REFERENCES crm.crm_accounts(id) ON DELETE RESTRICT,
  lead_id uuid REFERENCES crm.crm_leads(id) ON DELETE RESTRICT,
  opportunity_id uuid REFERENCES crm.crm_opportunities(id) ON DELETE RESTRICT,
  policy_id uuid REFERENCES crm.crm_account_followup_policies(id) ON DELETE RESTRICT,
  policy_occurrence_at timestamptz,
  assigned_user_id uuid NOT NULL,
  channel varchar(12) NOT NULL,
  due_at timestamptz NOT NULL,
  status varchar(22) NOT NULL,
  completed_at timestamptz,
  outcome text,
  next_followup_at timestamptz,
  CONSTRAINT ck_crm_followup_subject CHECK (num_nonnulls(account_id,lead_id,opportunity_id)>=1),
  CONSTRAINT ck_crm_followup_channel CHECK (channel IN ('EMAIL','SYSTEM','PHONE','VISIT')),
  CONSTRAINT ck_crm_followup_status CHECK (status IN ('Pending','Completed','Cancelled','Skipped','Overdue')),
  CONSTRAINT ck_crm_followup_completed CHECK ((status='Completed')=(completed_at IS NOT NULL)),
  CONSTRAINT ck_crm_followup_occurrence CHECK ((policy_id IS NULL)=(policy_occurrence_at IS NULL))
);
CREATE INDEX ix_crm_followups_assignee_status_due ON crm.crm_followups(assigned_user_id,status,due_at);
CREATE UNIQUE INDEX uq_crm_followup_policy_occurrence ON crm.crm_followups(policy_id,policy_occurrence_at) WHERE policy_id IS NOT NULL;
CREATE TABLE crm.crm_followup_history (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1,
  followup_id uuid NOT NULL REFERENCES crm.crm_followups(id) ON DELETE RESTRICT,
  action varchar(24) NOT NULL,
  actor_user_id uuid NOT NULL,
  reason text,
  previous_due_at timestamptz,
  new_due_at timestamptz
);
CREATE INDEX ix_crm_followup_history ON crm.crm_followup_history(followup_id,created_at);

CREATE TABLE crm.crm_reminders (
  id uuid PRIMARY KEY,
  created_by_user_id uuid NOT NULL,
  updated_by_user_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1,
  entity_type varchar(40) NOT NULL,
  entity_id uuid NOT NULL,
  recipient_user_id uuid NOT NULL,
  channel varchar(10) NOT NULL,
  notification_type varchar(160) NOT NULL,
  dedupe_key varchar(200) NOT NULL,
  fire_at timestamptz NOT NULL,
  dispatched_at timestamptz,
  status varchar(24) NOT NULL,
  attempt_count integer NOT NULL DEFAULT 0,
  target_version bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_crm_reminder_entity CHECK (entity_type IN ('Followup','Activity')),
  CONSTRAINT ck_crm_reminder_channel CHECK (channel IN ('Email','System')),
  CONSTRAINT ck_crm_reminder_state CHECK (status IN ('Pending','Queued','Cancelled','Failed')),
  CONSTRAINT ck_crm_reminder_attempt CHECK (attempt_count>=0)
);
CREATE UNIQUE INDEX uq_crm_reminder_dedupe ON crm.crm_reminders(dedupe_key);
CREATE INDEX ix_crm_reminder_status_fire ON crm.crm_reminders(status,fire_at);

CREATE TABLE crm.crm_outbox_messages (
  id uuid PRIMARY KEY,
  reminder_id uuid NOT NULL REFERENCES crm.crm_reminders(id) ON DELETE RESTRICT,
  event_name varchar(160) NOT NULL,
  status varchar(24) NOT NULL DEFAULT 'Pending',
  attempt_count integer NOT NULL DEFAULT 0,
  created_at timestamptz NOT NULL,
  next_attempt_at timestamptz NOT NULL,
  published_at timestamptz,
  lease_until timestamptz,
  last_error varchar(2000),
  CONSTRAINT ck_crm_outbox_state CHECK (status IN ('Pending','Leased','Published','DeadLetter','Cancelled'))
);
CREATE UNIQUE INDEX uq_crm_outbox_reminder ON crm.crm_outbox_messages(reminder_id);
CREATE INDEX ix_crm_outbox_ready ON crm.crm_outbox_messages(status,next_attempt_at);
CREATE TABLE crm.crm_inbox_messages (
  event_id uuid PRIMARY KEY,
  event_name varchar(160) NOT NULL,
  processed_at timestamptz NOT NULL
);
CREATE INDEX ix_crm_inbox_processed ON crm.crm_inbox_messages(processed_at);
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("CRM followups, reminder audit and delivery history cannot be destroyed automatically.");
}
