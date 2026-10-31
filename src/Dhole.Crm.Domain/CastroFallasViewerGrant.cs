namespace Dhole.Crm.Domain;

/// <summary>Global authorizations for accounts whose vendor is Castro Fallas.</summary>
public sealed class CastroFallasViewerGrant : CrmRecord
{
    public Guid UserId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
}
