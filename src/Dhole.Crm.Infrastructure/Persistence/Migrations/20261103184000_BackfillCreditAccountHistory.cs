using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Crm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Replays the authoritative finance audit trail into the unified account history.
/// Does not modify credit terms, dates or approval status.
/// </summary>
[DbContext(typeof(CrmDbContext))]
[Migration("20261103184000_BackfillCreditAccountHistory")]
public sealed class BackfillCreditAccountHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
INSERT INTO crm.crm_account_change_history
    (id, account_id, actor_user_id, action_code, area_code, changes_json,
     created_by_user_id, updated_by_user_id, created_at, updated_at, row_version)
SELECT h.id,
       p.account_id,
       NULLIF(h.changed_by, '00000000-0000-0000-0000-000000000000'::uuid),
       CASE
         WHEN h.new_values_json::jsonb ->> 'Status' = 'Approved' AND
              h.old_values_json::jsonb ->> 'Status' IS DISTINCT FROM 'Approved'
           THEN 'CREDIT_APPROVED'
         WHEN h.new_values_json::jsonb ->> 'Status' = 'Rejected'
           THEN 'CREDIT_REJECTED'
         WHEN h.new_values_json::jsonb ->> 'Status' = 'Superseded'
           THEN 'CREDIT_SUPERSEDED'
         WHEN LOWER(COALESCE(h.approval_reason,'')) LIKE '%coverage%' OR
              LOWER(COALESCE(h.approval_reason,'')) LIKE '%cobertura%'
           THEN 'CREDIT_COVERAGE_CHANGED'
         WHEN h.old_values_json::jsonb = '{}'::jsonb
           THEN 'CREDIT_REQUESTED'
         WHEN h.new_values_json::jsonb ->> 'Status' = 'PendingApproval'
           THEN 'CREDIT_REVISED'
         ELSE 'CREDIT_UPDATED'
       END,
       'CREDIT',
       COALESCE((
         SELECT jsonb_agg(jsonb_build_object(
             'field', fields.key,
             'before', old_json.data ->> fields.key,
             'after', new_json.data ->> fields.key
         ) ORDER BY fields.key)
         FROM (
           SELECT jsonb_object_keys(old_json.data) AS key
           UNION
           SELECT jsonb_object_keys(new_json.data) AS key
         ) fields
         WHERE old_json.data ->> fields.key IS DISTINCT FROM new_json.data ->> fields.key
       ), '[]'::jsonb)
       || CASE WHEN NULLIF(BTRIM(COALESCE(h.approval_reason,'')), '') IS NOT NULL
         THEN jsonb_build_array(jsonb_build_object(
           'field','ApprovalReason',
           'before', NULL,
           'after', h.approval_reason))
         ELSE '[]'::jsonb END,
       h.changed_by, NULL, h.changed_at, h.changed_at, 1
FROM crm.crm_credit_terms_history h
JOIN crm.crm_credit_profiles p ON p.id = h.credit_profile_id
CROSS JOIN LATERAL (
  SELECT COALESCE(NULLIF(h.old_values_json, '')::jsonb, '{}'::jsonb) AS data
) old_json
CROSS JOIN LATERAL (
  SELECT COALESCE(NULLIF(h.new_values_json, '')::jsonb, '{}'::jsonb) AS data
) new_json
ON CONFLICT (id) DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Financial audit events are immutable. Do not erase them on rollback.
    }
}
