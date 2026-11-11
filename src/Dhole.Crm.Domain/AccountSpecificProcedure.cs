namespace Dhole.Crm.Domain;

/// <summary>One of many account-specific operational procedures.</summary>
public sealed class AccountSpecificProcedure : CrmRecord
{
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public string? AssignedTeam { get; set; }
}
