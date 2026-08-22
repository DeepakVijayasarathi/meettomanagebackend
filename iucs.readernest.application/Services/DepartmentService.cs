using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Courses;
using iucs.readernest.domain.Entities.Academics;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class DepartmentService : IDepartmentService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;

        public DepartmentService(IUnitOfWork unitOfWork, IAuditLogService auditLog)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
        }

        public async Task<IReadOnlyList<DepartmentDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
        {
            var query = _unitOfWork.Repository<Department>().Query();
            if (!includeInactive)
            {
                query = query.Where(d => d.IsActive);
            }

            var departments = await query.OrderBy(d => d.Name).ToListAsync(cancellationToken);
            return departments.Select(ToDto).ToList();
        }

        public async Task<DepartmentDto> CreateAsync(SaveDepartmentRequest request, CancellationToken cancellationToken = default)
        {
            var name = request.Name.Trim();
            var repository = _unitOfWork.Repository<Department>();

            if (await repository.ExistsAsync(d => d.Name == name, cancellationToken))
            {
                throw new ConflictException($"A department named '{name}' already exists.");
            }

            var department = new Department
            {
                Name = name,
                Description = request.Description,
                IsActive = request.IsActive,
            };
            await repository.AddAsync(department, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(Department), department.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return ToDto(department);
        }

        public async Task<DepartmentDto> UpdateAsync(Guid id, SaveDepartmentRequest request, CancellationToken cancellationToken = default)
        {
            var repository = _unitOfWork.Repository<Department>();
            var department = await repository.GetByIdAsync(id, cancellationToken)
                ?? throw new NotFoundException(nameof(Department), id);

            var name = request.Name.Trim();
            if (!string.Equals(department.Name, name, StringComparison.Ordinal)
                && await repository.ExistsAsync(d => d.Name == name && d.Id != id, cancellationToken))
            {
                throw new ConflictException($"A department named '{name}' already exists.");
            }

            department.Name = name;
            department.Description = request.Description;
            department.IsActive = request.IsActive;
            repository.Update(department);

            await _auditLog.StageAsync(AuditAction.Update, nameof(Department), department.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return ToDto(department);
        }

        private static DepartmentDto ToDto(Department department) => new()
        {
            Id = department.Id,
            Name = department.Name,
            Description = department.Description,
            IsActive = department.IsActive,
        };
    }
}
