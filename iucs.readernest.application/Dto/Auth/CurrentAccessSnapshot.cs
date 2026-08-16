using iucs.readernest.domain.Enums;

namespace iucs.readernest.application.Dto.Auth
{
    /// <summary>Live account state, read fresh from the database — see IAuthService.GetCurrentAccessAsync.</summary>
    public class CurrentAccessSnapshot
    {
        public UserRole Role { get; set; }

        public UserStatus Status { get; set; }

        public IReadOnlyList<string> Permissions { get; set; } = [];
    }
}
