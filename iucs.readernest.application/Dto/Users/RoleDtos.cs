using System.ComponentModel.DataAnnotations;

namespace iucs.readernest.application.Dto.Users
{
    public class RoleDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string DisplayName { get; set; } = null!;

        public string? Description { get; set; }

        /// <summary>Route a user assigned this role lands on after login, e.g. "/subadmin/reports".</summary>
        public string? DefaultRoute { get; set; }

        public bool IsSystem { get; set; }

        public IReadOnlyList<PermissionDto> Permissions { get; set; } = [];
    }

    /// <remarks>Lengths mirror the RoleDefinition entity's columns — see SaveIntegrationRequest for why.</remarks>
    public class SaveRoleRequest
    {
        [Required]
        [MaxLength(64)]
        public string Name { get; set; } = null!;

        [Required]
        [MaxLength(100)]
        public string DisplayName { get; set; } = null!;

        [MaxLength(500)]
        public string? Description { get; set; }

        [MaxLength(200)]
        public string? DefaultRoute { get; set; }

        public List<PermissionDto> Permissions { get; set; } = [];
    }
}
