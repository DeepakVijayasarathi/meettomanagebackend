using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Auth
{
    /// <summary>Live account state, read fresh from the database — see IAuthService.GetCurrentAccessAsync.</summary>
    public class CurrentAccessSnapshot
    {
        public UserRole Role { get; set; }

        public UserStatus Status { get; set; }

        public IReadOnlyList<string> Permissions { get; set; } = [];
    }
}
