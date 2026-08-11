using iucs.readernest.application.Common;
using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Academics;
using iucs.readernest.domain.Entities.Academics;
using iucs.readernest.domain.Entities.Sessions;
using iucs.readernest.domain.Entities.Users;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class AcademicOpsService : IAcademicOpsService
    {
        private static readonly TimeSpan LeaveCutoff = TimeSpan.FromHours(6);

        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;
        private readonly INotificationService _notificationService;

        public AcademicOpsService(IUnitOfWork unitOfWork, IAuditLogService auditLog, INotificationService notificationService)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
            _notificationService = notificationService;
        }

        public async Task<IReadOnlyList<HolidayDto>> ListHolidaysAsync(CancellationToken cancellationToken = default)
        {
            var holidays = await _unitOfWork.Repository<Holiday>().Query()
                .OrderBy(h => h.Date)
                .ToListAsync(cancellationToken);
            return holidays.Select(ToDto).ToList();
        }

        public async Task<HolidayDto> CreateHolidayAsync(SaveHolidayRequest request, CancellationToken cancellationToken = default)
        {
            var exists = await _unitOfWork.Repository<Holiday>().ExistsAsync(h => h.Date == request.Date, cancellationToken);
            if (exists)
            {
                throw new ConflictException($"A holiday already exists on {request.Date:yyyy-MM-dd}.");
            }

            var holiday = new Holiday { Date = request.Date, Name = request.Name.Trim(), Description = request.Description };
            await _unitOfWork.Repository<Holiday>().AddAsync(holiday, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(Holiday), holiday.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Business rule: a class never runs on a holiday. Any session already scheduled
            // on this date is automatically carried forward to the next available same-weekday
            // slot (skipping further holidays), keeping the traceability link.
            var dayStart = request.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            var clashingSessions = await _unitOfWork.Repository<ClassSession>().TrackedQuery()
                .Where(s => (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.CarriedForward)
                            && s.ScheduledStartAtUtc >= dayStart
                            && s.ScheduledStartAtUtc < dayEnd)
                .ToListAsync(cancellationToken);

            // The next free same-weekday slot depends only on the holiday calendar, not on
            // any individual session, so it is computed once here rather than re-derived
            // inside the loop below — that probe was issuing a fresh ExistsAsync per session
            // per candidate week while always arriving at the same answer.
            var futureHolidayDates = (await _unitOfWork.Repository<Holiday>().Query()
                    .Where(h => h.Date > request.Date)
                    .Select(h => h.Date)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var offsetDays = 7;
            while (futureHolidayDates.Contains(request.Date.AddDays(offsetDays)))
            {
                offsetDays += 7;
            }

            foreach (var session in clashingSessions)
            {
                await _unitOfWork.Repository<ClassSession>().AddAsync(
                    new ClassSession
                    {
                        BatchId = session.BatchId,
                        TeacherProfileId = session.TeacherProfileId,
                        Type = session.Type,
                        Status = SessionStatus.CarriedForward,
                        ScheduledStartAtUtc = session.ScheduledStartAtUtc.AddDays(offsetDays),
                        ScheduledEndAtUtc = session.ScheduledEndAtUtc.AddDays(offsetDays),
                        MeetingRoomId = session.MeetingRoomId,
                        CarriedForwardFromSessionId = session.Id,
                    },
                    cancellationToken);

                session.Status = SessionStatus.Cancelled;
                session.CancellationReason = $"Holiday — {holiday.Name}; carried forward to {request.Date.AddDays(offsetDays):yyyy-MM-dd}";
            }

            if (clashingSessions.Count > 0)
            {
                await _auditLog.StageAsync(AuditAction.Update, nameof(ClassSession), null,
                    changesJson: $"{{\"holidayCarryForward\":{clashingSessions.Count}}}", cancellationToken: cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return ToDto(holiday);
        }

        public async Task DeleteHolidayAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var holiday = await _unitOfWork.Repository<Holiday>().GetByIdAsync(id, cancellationToken)
                ?? throw new NotFoundException(nameof(Holiday), id);

            _unitOfWork.Repository<Holiday>().Remove(holiday);
            await _auditLog.StageAsync(AuditAction.Delete, nameof(Holiday), id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<LeaveRequestDto> SubmitLeaveAsync(
            Guid teacherUserId,
            SubmitLeaveRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.EndAtUtc <= request.StartAtUtc)
            {
                throw new DomainValidationException("Leave end time must be after the start time.");
            }

            var teacher = await _unitOfWork.Repository<TeacherProfile>().Query()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.UserId == teacherUserId, cancellationToken)
                ?? throw new NotFoundException("No teacher profile is linked to the current account.");

            var affectedSessions = await CountAffectedSessionsAsync(teacher.Id, request.StartAtUtc, request.EndAtUtc, cancellationToken);

            // 6-hour rule: leave covering a session that starts within the cutoff is auto-blocked
            var cutoffLimit = DateTime.UtcNow.Add(LeaveCutoff);
            var blockingSession = await _unitOfWork.Repository<ClassSession>().Query()
                .Where(s => s.TeacherProfileId == teacher.Id
                            && s.Status == SessionStatus.Scheduled
                            && s.ScheduledStartAtUtc < cutoffLimit
                            && s.ScheduledStartAtUtc < request.EndAtUtc
                            && s.ScheduledEndAtUtc > request.StartAtUtc)
                .OrderBy(s => s.ScheduledStartAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (blockingSession is not null)
            {
                throw new DomainValidationException(
                    $"Leave cannot cover the session at {blockingSession.ScheduledStartAtUtc:u}: applications must be made at least 6 hours before a scheduled class.");
            }

            var leave = new LeaveRequest
            {
                TeacherProfileId = teacher.Id,
                StartAtUtc = request.StartAtUtc,
                EndAtUtc = request.EndAtUtc,
                Reason = request.Reason.Trim(),
            };
            await _unitOfWork.Repository<LeaveRequest>().AddAsync(leave, cancellationToken);
            await _auditLog.StageAsync(AuditAction.Create, nameof(LeaveRequest), leave.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Settings → Notifications → "Teacher leave requests" turns this alert off.
            if (await NotificationToggles.IsEnabledAsync(_unitOfWork, NotificationToggles.LeaveRequests, cancellationToken))
            {
                await NotifyAdminsAsync(
                    new Dictionary<string, string>
                    {
                        ["TeacherName"] = $"{teacher.User.FirstName} {teacher.User.LastName}",
                        ["StartAtLocal"] = request.StartAtUtc.ToString("u"),
                        ["EndAtLocal"] = request.EndAtUtc.ToString("u"),
                        ["AffectedSessions"] = affectedSessions.ToString(),
                        ["Reason"] = request.Reason,
                    },
                    cancellationToken);
            }

            leave.TeacherProfile = teacher;
            return await ToDtoAsync(leave, cancellationToken);
        }

        public async Task<IReadOnlyList<LeaveRequestDto>> ListLeaveAsync(
            LeaveStatus? status,
            CancellationToken cancellationToken = default)
        {
            IQueryable<LeaveRequest> query = _unitOfWork.Repository<LeaveRequest>().Query()
                .Include(l => l.TeacherProfile).ThenInclude(t => t.User);
            if (status.HasValue)
            {
                query = query.Where(l => l.Status == status.Value);
            }

            var leaves = await query.OrderByDescending(l => l.CreatedAtUtc).ToListAsync(cancellationToken);
            var result = new List<LeaveRequestDto>(leaves.Count);
            foreach (var leave in leaves)
            {
                result.Add(await ToDtoAsync(leave, cancellationToken));
            }

            return result;
        }

        public async Task<IReadOnlyList<LeaveRequestDto>> ListLeaveForTeacherUserAsync(
            Guid teacherUserId,
            CancellationToken cancellationToken = default)
        {
            var teacher = await _unitOfWork.Repository<TeacherProfile>()
                .FirstOrDefaultAsync(t => t.UserId == teacherUserId, cancellationToken)
                ?? throw new NotFoundException("No teacher profile is linked to the current account.");

            var leaves = await _unitOfWork.Repository<LeaveRequest>().Query()
                .Include(l => l.TeacherProfile).ThenInclude(t => t.User)
                .Where(l => l.TeacherProfileId == teacher.Id)
                .OrderByDescending(l => l.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            var result = new List<LeaveRequestDto>(leaves.Count);
            foreach (var leave in leaves)
            {
                result.Add(await ToDtoAsync(leave, cancellationToken));
            }

            return result;
        }

        public async Task<LeaveRequestDto> ReviewLeaveAsync(
            Guid id,
            ReviewLeaveRequest request,
            CancellationToken cancellationToken = default)
        {
            // Load tracked (Query() is AsNoTracking; mutating that never persists).
            var leave = await _unitOfWork.Repository<LeaveRequest>().FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(LeaveRequest), id);

            if (leave.Status != LeaveStatus.Pending)
            {
                throw new DomainValidationException($"This leave application is already {leave.Status}.");
            }

            leave.Status = request.Approve ? LeaveStatus.Approved : LeaveStatus.Rejected;
            leave.ReviewNote = request.ReviewNote;
            leave.ReviewedAtUtc = DateTime.UtcNow;

            // Approving leave must actually resolve the sessions it covers — previously
            // this only computed AffectedSessionCount for display and sent a notice; the
            // sessions themselves stayed Scheduled indefinitely for a teacher now on leave,
            // still blocking EnsureTeacherIsFreeAsync's slot checks and still completable.
            // This does NOT auto-reschedule a makeup class (unlike the no-show carry-forward
            // flow) — picking a new date/capacity for a makeup is an admin call, not one to
            // make silently here.
            var affectedCount = 0;
            if (leave.Status == LeaveStatus.Approved)
            {
                var affectedSessions = await _unitOfWork.Repository<ClassSession>().TrackedQuery()
                    .Where(s => s.TeacherProfileId == leave.TeacherProfileId
                        && s.Status == SessionStatus.Scheduled
                        && s.ScheduledStartAtUtc < leave.EndAtUtc
                        && s.ScheduledEndAtUtc > leave.StartAtUtc)
                    .ToListAsync(cancellationToken);
                affectedCount = affectedSessions.Count;

                foreach (var session in affectedSessions)
                {
                    session.Status = SessionStatus.Cancelled;
                    session.CancellationReason =
                        $"Teacher on approved leave ({leave.StartAtUtc:dd MMM yyyy} – {leave.EndAtUtc:dd MMM yyyy}).";
                }
            }

            await _auditLog.StageAsync(AuditAction.Update, nameof(LeaveRequest), leave.Id.ToString(),
                changesJson: affectedCount > 0 ? $"{{\"sessionsCancelled\":{affectedCount}}}" : null,
                cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // The review is committed from here on; the notification fan-out must not be
            // torn down by the caller aborting the HTTP request (browser closed/navigated),
            // or the teacher/team/parents silently miss the update mid-loop.
            cancellationToken = CancellationToken.None;

            var teacherProfile = await _unitOfWork.Repository<TeacherProfile>().Query()
                .Include(t => t.User)
                .FirstAsync(t => t.Id == leave.TeacherProfileId, cancellationToken);
            var teacherUser = teacherProfile.User;
            await _notificationService.SendTemplatedEmailAsync(
                teacherUser.Id,
                teacherUser.Email,
                NotificationType.LeaveStatusUpdate,
                "leave-status-teacher",
                new Dictionary<string, string>
                {
                    ["TeacherFirstName"] = teacherUser.FirstName,
                    ["StartAtLocal"] = leave.StartAtUtc.ToString("u"),
                    ["EndAtLocal"] = leave.EndAtUtc.ToString("u"),
                    ["Status"] = leave.Status.ToString(),
                    ["ReviewNote"] = string.IsNullOrEmpty(request.ReviewNote) ? "" : $"Note: {request.ReviewNote}",
                },
                cancellationToken);

            // Approved leave fans out: the whole core team plus every parent whose child
            // is in one of this teacher's batches gets notified (client requirement).
            if (leave.Status == LeaveStatus.Approved)
            {
                var teacherName = $"{teacherUser.FirstName} {teacherUser.LastName}";
                var window = $"{leave.StartAtUtc:dd MMM yyyy HH:mm} – {leave.EndAtUtc:dd MMM yyyy HH:mm} UTC";

                var coreTeam = await _unitOfWork.Repository<User>().Query()
                    .Where(u => (u.Role == UserRole.Admin || u.Role == UserRole.SubAdmin) && u.Status == UserStatus.Active)
                    .ToListAsync(cancellationToken);
                foreach (var member in coreTeam)
                {
                    await _notificationService.SendTemplatedEmailAsync(
                        member.Id, member.Email, NotificationType.LeaveStatusUpdate,
                        "leave-notify-core-team",
                        new Dictionary<string, string> { ["TeacherName"] = teacherName, ["Window"] = window },
                        cancellationToken);
                }

                var affectedParents = await _unitOfWork.Repository<BatchEnrollment>().Query()
                    .Where(e => e.Status == EnrollmentStatus.Active
                                && e.Batch.TeacherProfileId == leave.TeacherProfileId)
                    .Select(e => e.Child.ParentProfile.User)
                    .Distinct()
                    .ToListAsync(cancellationToken);
                foreach (var parent in affectedParents)
                {
                    await _notificationService.SendTemplatedEmailAsync(
                        parent.Id, parent.Email, NotificationType.LeaveStatusUpdate,
                        "leave-notify-parent",
                        new Dictionary<string, string> { ["TeacherName"] = teacherName, ["Window"] = window },
                        cancellationToken);
                }
            }

            // Attach the nav for DTO mapping only, after the last SaveChanges (avoids re-tracking).
            leave.TeacherProfile = teacherProfile;
            var dto = await ToDtoAsync(leave, cancellationToken);
            if (leave.Status == LeaveStatus.Approved)
            {
                // ToDtoAsync recomputes this by counting Scheduled sessions in the window —
                // now 0, since approval just cancelled them. Report what this approval
                // actually resolved, not a live count that undercounts its own effect.
                dto.AffectedSessionCount = affectedCount;
            }

            return dto;
        }

        public async Task<IReadOnlyList<SessionAttendanceDto>> CaptureAttendanceAsync(
            Guid sessionId,
            CaptureAttendanceRequest request,
            CancellationToken cancellationToken = default)
        {
            var session = await _unitOfWork.Repository<ClassSession>()
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
                ?? throw new NotFoundException(nameof(ClassSession), sessionId);

            if (session.Status is SessionStatus.Cancelled or SessionStatus.Rescheduled
                or SessionStatus.TeacherNoShow or SessionStatus.StudentNoShow)
            {
                throw new DomainValidationException(
                    $"Attendance cannot be recorded for a session in status '{session.Status}'.");
            }

            var repository = _unitOfWork.Repository<SessionAttendance>();

            // One tracked read of this session's attendance rows (bounded by class size),
            // instead of a FirstOrDefaultAsync per submitted entry. The whole-session read
            // is covered by the existing ClassSessionId index and lets the rejoin-updates-
            // the-existing-row rule below be resolved in memory.
            var existingRows = await repository.TrackedQuery()
                .Where(a => a.ClassSessionId == sessionId)
                .ToListAsync(cancellationToken);

            foreach (var entry in request.Entries)
            {
                if ((entry.ChildId is null) == (entry.TeacherProfileId is null))
                {
                    throw new DomainValidationException("Each attendance entry must set exactly one of childId or teacherProfileId.");
                }

                // Rejoin after a network drop updates the existing row, never duplicates it
                var existing = existingRows.FirstOrDefault(
                    a => a.ChildId == entry.ChildId && a.TeacherProfileId == entry.TeacherProfileId);

                if (existing is null)
                {
                    await repository.AddAsync(
                        new SessionAttendance
                        {
                            ClassSessionId = sessionId,
                            ParticipantType = entry.ChildId is not null ? ParticipantType.Student : ParticipantType.Teacher,
                            ChildId = entry.ChildId,
                            TeacherProfileId = entry.TeacherProfileId,
                            Status = entry.Status,
                            JoinedAtUtc = entry.JoinedAtUtc,
                            LeftAtUtc = entry.LeftAtUtc,
                        },
                        cancellationToken);
                }
                else
                {
                    existing.Status = entry.Status;
                    existing.JoinedAtUtc = entry.JoinedAtUtc ?? existing.JoinedAtUtc;
                    existing.LeftAtUtc = entry.LeftAtUtc ?? existing.LeftAtUtc;
                }
            }

            await _auditLog.StageAsync(AuditAction.Update, nameof(SessionAttendance), sessionId.ToString(),
                changesJson: $"{{\"entries\":{request.Entries.Count}}}", cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Attendance updates: parents hear about an absence the moment it is recorded
            var absentChildIds = request.Entries
                .Where(e => e.ChildId.HasValue && e.Status == AttendanceStatus.Absent)
                .Select(e => e.ChildId!.Value)
                .ToList();
            if (absentChildIds.Count > 0)
            {
                var absentChildren = await _unitOfWork.Repository<Child>().Query()
                    .Include(c => c.ParentProfile).ThenInclude(p => p.User)
                    .Where(c => absentChildIds.Contains(c.Id))
                    .ToListAsync(cancellationToken);
                foreach (var child in absentChildren)
                {
                    var parentUser = child.ParentProfile.User;
                    await _notificationService.SendTemplatedEmailAsync(
                        parentUser.Id,
                        parentUser.Email,
                        NotificationType.AttendanceUpdate,
                        "attendance-absent",
                        new Dictionary<string, string> { ["ChildFirstName"] = child.FirstName },
                        cancellationToken);
                }
            }

            return await ListAttendanceAsync(sessionId, cancellationToken);
        }

        public async Task<IReadOnlyList<SessionAttendanceDto>> ListAttendanceAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            var rows = await _unitOfWork.Repository<SessionAttendance>().Query()
                .Include(a => a.Child)
                .Where(a => a.ClassSessionId == sessionId)
                .ToListAsync(cancellationToken);

            return rows.Select(a => new SessionAttendanceDto
            {
                Id = a.Id,
                ClassSessionId = a.ClassSessionId,
                ParticipantType = a.ParticipantType,
                ChildId = a.ChildId,
                ChildName = a.Child is null ? null : $"{a.Child.FirstName} {a.Child.LastName}",
                TeacherProfileId = a.TeacherProfileId,
                Status = a.Status,
                JoinedAtUtc = a.JoinedAtUtc,
                LeftAtUtc = a.LeftAtUtc,
            }).ToList();
        }

        private async Task<int> CountAffectedSessionsAsync(
            Guid teacherProfileId,
            DateTime startUtc,
            DateTime endUtc,
            CancellationToken cancellationToken)
        {
            return await _unitOfWork.Repository<ClassSession>().Query()
                .CountAsync(
                    s => s.TeacherProfileId == teacherProfileId
                         && s.Status == SessionStatus.Scheduled
                         && s.ScheduledStartAtUtc < endUtc
                         && s.ScheduledEndAtUtc > startUtc,
                    cancellationToken);
        }

        private async Task NotifyAdminsAsync(IReadOnlyDictionary<string, string> tokens, CancellationToken cancellationToken)
        {
            var admins = await _unitOfWork.Repository<User>().Query()
                .Where(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);
            foreach (var admin in admins)
            {
                await _notificationService.SendTemplatedEmailAsync(
                    admin.Id, admin.Email, NotificationType.General, "leave-submitted-admin-alert", tokens, cancellationToken);
            }
        }

        private async Task<LeaveRequestDto> ToDtoAsync(LeaveRequest leave, CancellationToken cancellationToken)
        {
            return new LeaveRequestDto
            {
                Id = leave.Id,
                TeacherProfileId = leave.TeacherProfileId,
                TeacherName = $"{leave.TeacherProfile.User.FirstName} {leave.TeacherProfile.User.LastName}",
                StartAtUtc = leave.StartAtUtc,
                EndAtUtc = leave.EndAtUtc,
                Reason = leave.Reason,
                Status = leave.Status,
                ReviewNote = leave.ReviewNote,
                CreatedAtUtc = leave.CreatedAtUtc,
                AffectedSessionCount = await CountAffectedSessionsAsync(
                    leave.TeacherProfileId, leave.StartAtUtc, leave.EndAtUtc, cancellationToken),
            };
        }

        private static HolidayDto ToDto(Holiday holiday)
        {
            return new HolidayDto
            {
                Id = holiday.Id,
                Date = holiday.Date,
                Name = holiday.Name,
                Description = holiday.Description,
            };
        }
    }
}
