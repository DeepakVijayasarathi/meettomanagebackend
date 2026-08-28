using iucs.meettomanage.domain.Enums;

namespace iucs.meettomanage.application.Dto.Users
{
    public class PermissionDto
    {
        public PermissionModule Module { get; set; }

        public bool CanView { get; set; }

        public bool CanCreate { get; set; }

        public bool CanEdit { get; set; }

        public bool CanDelete { get; set; }

        public bool CanApprove { get; set; }
    }
}
