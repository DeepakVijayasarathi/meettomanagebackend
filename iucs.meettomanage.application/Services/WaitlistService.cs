using System.ComponentModel.DataAnnotations;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace iucs.meettomanage.application.Services
{
    public class WaitlistEntryDto
    {
        public Guid Id { get; set; }
        public Guid BatchId { get; set; }
        public string BatchName { get; set; } = null!;
        public string CourseName { get; set; } = null!;
        public Guid ChildId { get; set; }
        public string ChildName { get; set; } = null!;
        public string? ParentName { get; set; }
        public string? ParentEmail { get; set; }
        public string? ParentPhone { get; set; }
        public WaitlistStatus Status { get; set; }
        public int Position { get; set; }
        public int SeatsFree { get; set; }
        public string? Note { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? OfferedAtUtc { get; set; }
    }

    public class AddToWaitlistRequest
    {
        [Required] public Guid BatchId { get; set; }
        [Required] public Guid ChildId { get; set; }
        [StringLength(500)] public string? Note { get; set; }
    }

    public interface IWaitlistService
    {
        Task<IReadOnlyList<WaitlistEntryDto>> ListAsync(Guid? batchId, CancellationToken cancellationToken = default);
        Task<WaitlistEntryDto> AddAsync(AddToWaitlistRequest request, CancellationToken cancellationToken = default);
        Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
        /// <summary>Enrols the waiting student (normal capacity rules apply) and closes the entry.</summary>
        Task<WaitlistEntryDto> EnrollAsync(Guid id, CancellationToken cancellationToken = default);
        /// <summary>Marks the next waiting students as offered wherever a batch has free seats, and tells families and staff. Returns how many were offered.</summary>
        Task<int> OfferOpenSeatsAsync(CancellationToken cancellationToken = default);
    }

    public class WaitlistService(IUnitOfWork unitOfWork, IBatchService batches, INotificationService notifications, ILogger<WaitlistService> logger) : IWaitlistService
    {
        public async Task<IReadOnlyList<WaitlistEntryDto>> ListAsync(Guid? batchId, CancellationToken cancellationToken = default)
        {
            var query = unitOfWork.Repository<BatchWaitlistEntry>().Query()
                .Where(e => e.Status == WaitlistStatus.Waiting || e.Status == WaitlistStatus.Offered);
            if (batchId is not null) query = query.Where(e => e.BatchId == batchId);
            var rows = await query.OrderBy(e => e.CreatedAtUtc).Select(e => new WaitlistEntryDto
            {
                Id = e.Id, BatchId = e.BatchId, BatchName = e.Batch.Name, CourseName = e.Batch.Course.Name, ChildId = e.ChildId,
                ChildName = e.Child.FirstName + " " + e.Child.LastName,
                ParentName = e.Child.ParentProfile.User.FirstName + " " + e.Child.ParentProfile.User.LastName,
                ParentEmail = e.Child.ParentProfile.User.Email, ParentPhone = e.Child.ParentProfile.User.Phone,
                Status = e.Status, Note = e.Note, CreatedAtUtc = e.CreatedAtUtc, OfferedAtUtc = e.OfferedAtUtc,
                SeatsFree = e.Batch.Capacity - e.Batch.Enrollments.Count(x => x.Status == EnrollmentStatus.Active),
            }).ToListAsync(cancellationToken);
            foreach (var group in rows.GroupBy(r => r.BatchId))
            {
                var position = 1;
                foreach (var row in group) row.Position = position++;
            }
            foreach (var row in rows) row.SeatsFree = Math.Max(0, row.SeatsFree);
            return rows;
        }

        public async Task<WaitlistEntryDto> AddAsync(AddToWaitlistRequest request, CancellationToken cancellationToken = default)
        {
            var batch = await unitOfWork.Repository<Batch>().Query().FirstOrDefaultAsync(b => b.Id == request.BatchId, cancellationToken)
                ?? throw new NotFoundException("Batch not found.");
            if (batch.Status != BatchStatus.Active) throw new DomainValidationException($"Batch '{batch.Name}' isn't taking students.");
            if (!await unitOfWork.Repository<Child>().Query().AnyAsync(c => c.Id == request.ChildId, cancellationToken))
                throw new NotFoundException("Student not found.");
            if (await unitOfWork.Repository<BatchEnrollment>().Query().AnyAsync(e => e.BatchId == request.BatchId && e.ChildId == request.ChildId && e.Status == EnrollmentStatus.Active, cancellationToken))
                throw new ConflictException("This student is already enrolled in that batch.");

            var entry = await unitOfWork.Repository<BatchWaitlistEntry>().TrackedQuery()
                .FirstOrDefaultAsync(e => e.BatchId == request.BatchId && e.ChildId == request.ChildId, cancellationToken);
            if (entry is { Status: WaitlistStatus.Waiting or WaitlistStatus.Offered })
                throw new ConflictException("This student is already on the waitlist for that batch.");
            if (entry is null)
            {
                entry = new BatchWaitlistEntry { BatchId = request.BatchId, ChildId = request.ChildId };
                await unitOfWork.Repository<BatchWaitlistEntry>().AddAsync(entry, cancellationToken);
            }
            else
            {
                // Rejoining after being removed or enrolled before: back of the queue.
                entry.Status = WaitlistStatus.Waiting;
                entry.OfferedAtUtc = null;
                entry.ClosedAtUtc = null;
                entry.CreatedAtUtc = DateTime.UtcNow;
            }
            entry.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return (await ListAsync(request.BatchId, cancellationToken)).Single(e => e.Id == entry.Id);
        }

        public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var entry = await OpenEntryAsync(id, cancellationToken);
            entry.Status = WaitlistStatus.Removed;
            entry.ClosedAtUtc = DateTime.UtcNow;
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<WaitlistEntryDto> EnrollAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var snapshot = (await ListAsync(null, cancellationToken)).FirstOrDefault(e => e.Id == id)
                ?? throw new NotFoundException("Waitlist entry not found.");
            await batches.AssignStudentAsync(snapshot.BatchId, snapshot.ChildId, cancellationToken);
            var entry = await OpenEntryAsync(id, cancellationToken);
            entry.Status = WaitlistStatus.Enrolled;
            entry.ClosedAtUtc = DateTime.UtcNow;
            await unitOfWork.SaveChangesAsync(cancellationToken);
            snapshot.Status = WaitlistStatus.Enrolled;
            return snapshot;
        }

        public async Task<int> OfferOpenSeatsAsync(CancellationToken cancellationToken = default)
        {
            var open = await unitOfWork.Repository<Batch>().Query()
                .Where(b => b.Status == BatchStatus.Active
                            && unitOfWork.Repository<BatchWaitlistEntry>().Query().Any(w => w.BatchId == b.Id && w.Status == WaitlistStatus.Waiting))
                .Select(b => new
                {
                    b.Id, b.Name,
                    Free = b.Capacity - b.Enrollments.Count(e => e.Status == EnrollmentStatus.Active),
                    Offered = unitOfWork.Repository<BatchWaitlistEntry>().Query().Count(w => w.BatchId == b.Id && w.Status == WaitlistStatus.Offered),
                })
                .ToListAsync(cancellationToken);

            var offered = new List<(BatchWaitlistEntry Entry, string BatchName, string ChildFirst, Guid ParentUserId)>();
            foreach (var batch in open.Where(b => b.Free - b.Offered > 0))
            {
                var next = await unitOfWork.Repository<BatchWaitlistEntry>().TrackedQuery()
                    .Include(w => w.Child).ThenInclude(c => c.ParentProfile)
                    .Where(w => w.BatchId == batch.Id && w.Status == WaitlistStatus.Waiting)
                    .OrderBy(w => w.CreatedAtUtc).Take(batch.Free - batch.Offered)
                    .ToListAsync(cancellationToken);
                foreach (var entry in next)
                {
                    entry.Status = WaitlistStatus.Offered;
                    entry.OfferedAtUtc = DateTime.UtcNow;
                    offered.Add((entry, batch.Name, entry.Child.FirstName, entry.Child.ParentProfile.UserId));
                }
            }
            if (offered.Count == 0) return 0;
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var staff = await unitOfWork.Repository<User>().Query()
                .Where(u => u.Status == UserStatus.Active && (u.Role == UserRole.Admin || u.Role == UserRole.AdmissionTeam))
                .Select(u => u.Id).ToListAsync(cancellationToken);
            foreach (var (entry, batchName, childFirst, parentUserId) in offered)
            {
                try
                {
                    await notifications.NotifyInAppAsync(parentUserId, NotificationType.General, $"A seat opened in {batchName}",
                        $"Good news — a seat is free in {batchName} for {childFirst}. The academy will confirm the place with you shortly.",
                        NotificationTargets.Class, null, cancellationToken);
                    foreach (var userId in staff)
                        await notifications.NotifyInAppAsync(userId, NotificationType.General, $"Waitlist: seat free in {batchName}",
                            $"{childFirst} is next on the waitlist for {batchName}. Enrol them from Waitlist.", null, null, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Waitlist notification failed for entry {EntryId}", entry.Id);
                }
            }
            return offered.Count;
        }

        private async Task<BatchWaitlistEntry> OpenEntryAsync(Guid id, CancellationToken cancellationToken) =>
            await unitOfWork.Repository<BatchWaitlistEntry>().TrackedQuery()
                .FirstOrDefaultAsync(e => e.Id == id && (e.Status == WaitlistStatus.Waiting || e.Status == WaitlistStatus.Offered), cancellationToken)
            ?? throw new NotFoundException("Waitlist entry not found.");
    }
}
