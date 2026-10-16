namespace iucs.meettomanage.domain.Entities.Tenancy;

/// <summary>Transactional tenant outbox: no passwords or profile data in messages.</summary>
public sealed class LoginDirectoryChange
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
}
