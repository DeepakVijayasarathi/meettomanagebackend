using iucs.readernest.application.Dto.Courses;

namespace iucs.readernest.application.Services
{
    public interface IDepartmentService
    {
        Task<IReadOnlyList<DepartmentDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

        Task<DepartmentDto> CreateAsync(SaveDepartmentRequest request, CancellationToken cancellationToken = default);

        Task<DepartmentDto> UpdateAsync(Guid id, SaveDepartmentRequest request, CancellationToken cancellationToken = default);
    }
}
