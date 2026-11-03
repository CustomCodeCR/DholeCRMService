namespace Dhole.Crm.Domain;

/// <summary>
/// Immutable account creation and change event. Stored in the CRM database,
/// independently of the account's current row version.
/// </summary>
public sealed class AccountChangeHistory : CrmRecord
{
    public Guid AccountId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActionCode { get; set; } = "";
    public string AreaCode { get; set; } = "";
    public string ChangesJson { get; set; } = "[]";
}

public sealed record AccountFieldChange(string Field, string? Before, string? After);
