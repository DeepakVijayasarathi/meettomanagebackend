using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.domain.Entities.Academics;
using iucs.meettomanage.domain.Entities.Learning;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace iucs.meettomanage.application.Services
{
    public class CertificateDto
    {
        public Guid Id { get; set; }
        public Guid ChildId { get; set; }
        public Guid? BatchId { get; set; }
        public string Code { get; set; } = null!;
        public string StudentName { get; set; } = null!;
        public string CourseName { get; set; } = null!;
        public string? TeacherName { get; set; }
        public string? BatchName { get; set; }
        public DateTime IssuedAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
        public string? RevokedReason { get; set; }
    }

    /// <summary>What anyone holding the QR code may see: enough to confirm the certificate, nothing more.</summary>
    public class CertificateVerificationDto
    {
        public bool Valid { get; set; }
        public string Code { get; set; } = null!;
        public string StudentName { get; set; } = null!;
        public string CourseName { get; set; } = null!;
        public DateTime IssuedAtUtc { get; set; }
        public bool Revoked { get; set; }
    }

    public class IssueCertificateRequest
    {
        [Required] public Guid ChildId { get; set; }
        public Guid? BatchId { get; set; }
        /// <summary>Required when there is no batch; otherwise the batch's course name is used.</summary>
        [StringLength(200)] public string? CourseName { get; set; }
    }

    public class RevokeCertificateRequest
    {
        [StringLength(300)] public string? Reason { get; set; }
    }

    public interface ICertificateService
    {
        /// <summary>Issues certificates to every student who finished a completed batch and doesn't have one yet. Returns how many were issued.</summary>
        Task<int> IssueForCompletedBatchesAsync(bool notify, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CertificateDto>> ListAsync(Guid? batchId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CertificateDto>> ListForParentAsync(Guid parentUserId, CancellationToken cancellationToken = default);
        Task<CertificateDto> IssueAsync(IssueCertificateRequest request, CancellationToken cancellationToken = default);
        Task<CertificateDto> RevokeAsync(Guid id, string? reason, CancellationToken cancellationToken = default);
        Task<CertificateDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<CertificateDto> GetForParentAsync(Guid parentUserId, Guid id, CancellationToken cancellationToken = default);
        Task<CertificateVerificationDto?> VerifyAsync(string code, CancellationToken cancellationToken = default);
    }

    public class CertificateService(IUnitOfWork unitOfWork, INotificationService notifications, ILogger<CertificateService> logger) : ICertificateService
    {
        // No 0/O or 1/I/L, so a code read off paper is typed back correctly.
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        public async Task<int> IssueForCompletedBatchesAsync(bool notify, CancellationToken cancellationToken = default)
        {
            // Everyone still on the roll when the batch finished (Active) or marked done (Completed); not those who withdrew.
            var due = await unitOfWork.Repository<BatchEnrollment>().Query()
                .Where(e => e.Batch.CompletedAtUtc != null
                            && e.Status != EnrollmentStatus.Withdrawn
                            && !unitOfWork.Repository<Certificate>().Query().Any(c => c.ChildId == e.ChildId && c.BatchId == e.BatchId))
                .Select(e => new
                {
                    e.ChildId, e.BatchId, e.Child.FirstName, e.Child.LastName, CourseName = e.Batch.Course.Name,
                    TeacherFirst = e.Batch.TeacherProfile.User.FirstName, TeacherLast = e.Batch.TeacherProfile.User.LastName,
                    ParentUserId = e.Child.ParentProfile.UserId, CompletedAtUtc = e.Batch.CompletedAtUtc!.Value,
                })
                .Take(500)
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var issued = new List<(Certificate Certificate, Guid ParentUserId, DateTime CompletedAtUtc)>();
            foreach (var row in due)
            {
                var certificate = new Certificate
                {
                    ChildId = row.ChildId,
                    BatchId = row.BatchId,
                    Code = await NewCodeAsync(cancellationToken),
                    StudentName = $"{row.FirstName} {row.LastName}".Trim(),
                    CourseName = row.CourseName,
                    TeacherName = $"{row.TeacherFirst} {row.TeacherLast}".Trim(),
                    IssuedAtUtc = DateTime.UtcNow,
                };
                await unitOfWork.Repository<Certificate>().AddAsync(certificate, cancellationToken);
                issued.Add((certificate, row.ParentUserId, row.CompletedAtUtc));
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);

            if (notify)
            {
                // Only for recent completions, so a first run doesn't message families about courses finished long ago.
                foreach (var (certificate, parentUserId, completedAtUtc) in issued.Where(x => x.CompletedAtUtc >= DateTime.UtcNow.AddDays(-14)))
                {
                    try
                    {
                        await notifications.NotifyInAppAsync(parentUserId, NotificationType.General,
                            $"Certificate ready: {certificate.CourseName}",
                            $"Congratulations! {certificate.StudentName.Split(' ')[0]} completed {certificate.CourseName}. The certificate is ready to download and share.",
                            NotificationTargets.Certificate, certificate.Id, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Certificate notification failed for {CertificateId}", certificate.Id);
                    }
                }
            }
            return issued.Count;
        }

        public async Task<IReadOnlyList<CertificateDto>> ListAsync(Guid? batchId, CancellationToken cancellationToken = default)
        {
            var query = Query();
            if (batchId is not null) query = query.Where(c => c.BatchId == batchId);
            return await query.OrderByDescending(c => c.IssuedAtUtc).Take(500).Select(Projection).ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<CertificateDto>> ListForParentAsync(Guid parentUserId, CancellationToken cancellationToken = default) =>
            await Query().Where(c => c.Child.ParentProfile.UserId == parentUserId && c.RevokedAtUtc == null)
                .OrderByDescending(c => c.IssuedAtUtc).Select(Projection).ToListAsync(cancellationToken);

        public async Task<CertificateDto> IssueAsync(IssueCertificateRequest request, CancellationToken cancellationToken = default)
        {
            var child = await unitOfWork.Repository<Child>().Query().FirstOrDefaultAsync(c => c.Id == request.ChildId, cancellationToken)
                ?? throw new NotFoundException("Student not found.");
            string courseName;
            string? teacherName = null;
            if (request.BatchId is { } batchId)
            {
                var batch = await unitOfWork.Repository<Batch>().Query()
                    .Where(b => b.Id == batchId)
                    .Select(b => new { b.Course.Name, b.TeacherProfile.User.FirstName, b.TeacherProfile.User.LastName })
                    .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException("Batch not found.");
                if (!await unitOfWork.Repository<BatchEnrollment>().Query().AnyAsync(e => e.BatchId == batchId && e.ChildId == child.Id, cancellationToken))
                    throw new DomainValidationException("This student isn't enrolled in that batch.");
                var existing = await unitOfWork.Repository<Certificate>().TrackedQuery()
                    .FirstOrDefaultAsync(c => c.ChildId == child.Id && c.BatchId == batchId, cancellationToken);
                if (existing is not null)
                {
                    if (existing.RevokedAtUtc is null) throw new ConflictException("This student already has a certificate for that batch.");
                    // Re-issuing a revoked certificate restores it under the same code.
                    existing.RevokedAtUtc = null;
                    existing.RevokedReason = null;
                    existing.IssuedAtUtc = DateTime.UtcNow;
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return await GetAsync(existing.Id, cancellationToken);
                }
                courseName = request.CourseName?.Trim() is { Length: > 0 } custom ? custom : batch.Name;
                teacherName = $"{batch.FirstName} {batch.LastName}".Trim();
            }
            else
            {
                courseName = request.CourseName?.Trim() is { Length: > 0 } name ? name
                    : throw new DomainValidationException("Enter the course name for a certificate without a batch.");
            }

            var certificate = new Certificate
            {
                ChildId = child.Id,
                BatchId = request.BatchId,
                Code = await NewCodeAsync(cancellationToken),
                StudentName = $"{child.FirstName} {child.LastName}".Trim(),
                CourseName = courseName,
                TeacherName = teacherName,
                IssuedAtUtc = DateTime.UtcNow,
            };
            await unitOfWork.Repository<Certificate>().AddAsync(certificate, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await GetAsync(certificate.Id, cancellationToken);
        }

        public async Task<CertificateDto> RevokeAsync(Guid id, string? reason, CancellationToken cancellationToken = default)
        {
            var certificate = await unitOfWork.Repository<Certificate>().TrackedQuery().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                ?? throw new NotFoundException("Certificate not found.");
            certificate.RevokedAtUtc ??= DateTime.UtcNow;
            certificate.RevokedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await GetAsync(id, cancellationToken);
        }

        public async Task<CertificateDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            await Query().Where(c => c.Id == id).Select(Projection).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Certificate not found.");

        public async Task<CertificateDto> GetForParentAsync(Guid parentUserId, Guid id, CancellationToken cancellationToken = default) =>
            await Query().Where(c => c.Id == id && c.Child.ParentProfile.UserId == parentUserId && c.RevokedAtUtc == null)
                .Select(Projection).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Certificate not found.");

        public async Task<CertificateVerificationDto?> VerifyAsync(string code, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(code);
            if (normalized.Length is < 6 or > 16) return null;
            var certificate = await unitOfWork.Repository<Certificate>().Query().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
            if (certificate is null) return null;
            return new CertificateVerificationDto
            {
                Valid = certificate.RevokedAtUtc is null,
                Revoked = certificate.RevokedAtUtc is not null,
                Code = certificate.Code,
                StudentName = PublicName(certificate.StudentName),
                CourseName = certificate.CourseName,
                IssuedAtUtc = certificate.IssuedAtUtc,
            };
        }

        /// <summary>"Asha Kumar" → "Asha K." so the public page confirms the holder without exposing a child's full name.</summary>
        public static string PublicName(string fullName)
        {
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "",
                1 => parts[0],
                _ => $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.",
            };
        }

        public static string Normalize(string code) =>
            new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private IQueryable<Certificate> Query() => unitOfWork.Repository<Certificate>().Query();

        private static readonly System.Linq.Expressions.Expression<Func<Certificate, CertificateDto>> Projection = c => new CertificateDto
        {
            Id = c.Id, ChildId = c.ChildId, BatchId = c.BatchId, Code = c.Code, StudentName = c.StudentName,
            CourseName = c.CourseName, TeacherName = c.TeacherName, BatchName = c.Batch != null ? c.Batch.Name : null,
            IssuedAtUtc = c.IssuedAtUtc, RevokedAtUtc = c.RevokedAtUtc, RevokedReason = c.RevokedReason,
        };

        private async Task<string> NewCodeAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var bytes = RandomNumberGenerator.GetBytes(10);
                var code = new string(bytes.Select(b => CodeAlphabet[b % CodeAlphabet.Length]).ToArray());
                if (!await unitOfWork.Repository<Certificate>().Query().AnyAsync(c => c.Code == code, cancellationToken))
                    return code;
            }
            throw new InvalidOperationException("Could not create a unique certificate code.");
        }
    }
}
