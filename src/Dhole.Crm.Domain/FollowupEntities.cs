namespace Dhole.Crm.Domain;
// CRM owns policies, follow-ups and reminder intent. Notifications owns actual delivery.
public sealed class AccountFollowupPolicy : CrmRecord
{
    public Guid AccountId { get; set; }
    public Guid OwnerUserId { get; set; }
    public bool Enabled { get; set; } = true;
    public string ChannelStrategy { get; set; } = "BOTH";
    public int FrequencyDays { get; set; } = 7;
    public DateTime NextDueAt { get; set; }
    public string TimezoneId { get; set; } = "America/Costa_Rica";
    public int ReminderLeadMinutes { get; set; } = 60;
    public DateTime? PausedUntil { get; set; }
}
public sealed class Followup : CrmRecord
{
    public Guid? AccountId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? PolicyId { get; set; }
    public DateTime? PolicyOccurrenceAt { get; set; }
    public Guid AssignedUserId { get; set; }
    public string Channel { get; set; } = "SYSTEM";
    public DateTime DueAt { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public DateTime? NextFollowupAt { get; set; }
}
public sealed class FollowupHistory : CrmRecord
{
    public Guid FollowupId { get; set; }
    public string Action { get; set; } = "";
    public Guid ActorUserId { get; set; }
    public string? Reason { get; set; }
    public DateTime? PreviousDueAt { get; set; }
    public DateTime? NewDueAt { get; set; }
}
public sealed class CrmReminder : CrmRecord
{
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid RecipientUserId { get; set; }
    public string Channel { get; set; } = "System";
    public string NotificationType { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public DateTime FireAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public long TargetVersion { get; set; }
}
public sealed class CrmOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReminderId { get; set; }
    public string EventName { get; set; } = "notifications.notification.requested";
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LastError { get; set; }
}
public sealed class CrmInboxMessage
{
    public Guid EventId { get; set; }
    public string EventName { get; set; } = "";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
