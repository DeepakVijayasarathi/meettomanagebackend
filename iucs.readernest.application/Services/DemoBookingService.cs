using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Common.Interfaces;
using iucs.readernest.application.Dto.Admission;
using iucs.readernest.application.Helper;
using iucs.readernest.application.Mappings;
using iucs.readernest.domain.Entities.Admission;
using iucs.readernest.domain.Entities.Integrations;
using iucs.readernest.domain.Entities.Sessions;
using iucs.readernest.domain.Entities.Users;
using iucs.readernest.domain.Enums;
using iucs.readernest.domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace iucs.readernest.application.Services
{
    public class DemoBookingService : IDemoBookingService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditLogService _auditLog;
        private readonly ICrmNotifier _crmNotifier;
        private readonly IEmailSender _emailSender;
        private readonly IEmailTemplateService _emailTemplateService;
        private readonly IJitsiTokenService _jitsiTokenService;

        public DemoBookingService(
            IUnitOfWork unitOfWork,
            IAuditLogService auditLog,
            IEmailSender emailSender,
            IEmailTemplateService emailTemplateService,
            ICrmNotifier crmNotifier,
            IJitsiTokenService jitsiTokenService)
        {
            _unitOfWork = unitOfWork;
            _auditLog = auditLog;
            _emailSender = emailSender;
            _emailTemplateService = emailTemplateService;
            _crmNotifier = crmNotifier;
            _jitsiTokenService = jitsiTokenService;
        }

        public async Task<IReadOnlyList<DemoBookingDto>> ListAsync(
            ConversionStatus? status,
            CancellationToken cancellationToken = default)
        {
            var query = BaseQuery();
            if (status.HasValue)
            {
                query = query.Where(b => b.ConversionStatus == status.Value);
            }

            var bookings = await query.OrderByDescending(b => b.CreatedAtUtc).ToListAsync(cancellationToken);
            return bookings.Select(b => b.ToDto()).ToList();
        }

        public async Task<DemoBookingDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var booking = await BaseQuery().FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
                ?? throw new NotFoundException(nameof(DemoBooking), id);

            return booking.ToDto();
        }

        public async Task<DemoBookingDto> CreateAsync(
            CreateDemoBookingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ScheduledEndAtUtc <= request.ScheduledStartAtUtc)
            {
                throw new DomainValidationException("Demo end time must be after the start time.");
            }

            // Picking a free teacher and booking them into the slot is one indivisible decision:
            // the "nobody overlaps this slot" read is only worth anything if no one else can
            // slip a conflicting session in before this one is committed. There is no single
            // row to lock (the conflict is a range overlap across rows, not a duplicate key),
            // so this runs SERIALIZABLE and lets PostgreSQL's SSI arbitrate, retrying from the
            // top on a serialization failure. Nothing irreversible — emails, the CRM push —
            // happens inside; a retry must be free to redo the whole thing.
            var (session, booking) = await _unitOfWork.ExecuteInSerializableTransactionAsync(async ct =>
            {
                Guid teacherProfileId;
                if (request.TeacherProfileId.HasValue)
                {
                    var teacherExists = await _unitOfWork.Repository<TeacherProfile>()
                        .ExistsAsync(t => t.Id == request.TeacherProfileId.Value, ct);
                    if (!teacherExists)
                    {
                        throw new NotFoundException(nameof(TeacherProfile), request.TeacherProfileId.Value);
                    }

                    teacherProfileId = request.TeacherProfileId.Value;
                }
                else
                {
                    teacherProfileId = await AutoAssignTeacherAsync(request, ct);
                }

                // Demos are always one-time sessions, never recurring, and have no batch
                var newSession = new ClassSession
                {
                    TeacherProfileId = teacherProfileId,
                    Type = SessionType.Demo,
                    ScheduledStartAtUtc = request.ScheduledStartAtUtc,
                    ScheduledEndAtUtc = request.ScheduledEndAtUtc,
                    MeetingRoomId = $"trn-demo-{Guid.NewGuid():N}",
                };
                await _unitOfWork.Repository<ClassSession>().AddAsync(newSession, ct);

                var newBooking = new DemoBooking
                {
                    ClassSession = newSession,
                    ParentName = request.ParentName.Trim(),
                    ParentEmail = request.ParentEmail.Trim().ToLowerInvariant(),
                    ParentPhone = request.ParentPhone,
                    ChildName = request.ChildName.Trim(),
                    ChildAge = request.ChildAge,
                    Department = request.Department,
                    Participants = request.Participants
                        .Select(p =>
                        {
                            // Adults need an email for the confirmation; children carry none.
                            if (!p.IsChild && string.IsNullOrWhiteSpace(p.Email))
                            {
                                throw new DomainValidationException($"Participant '{p.Name}' needs an email address (children don't).");
                            }

                            return new DemoParticipant
                            {
                                Name = p.Name.Trim(),
                                Email = string.IsNullOrWhiteSpace(p.Email) ? null : p.Email.Trim().ToLowerInvariant(),
                                Phone = p.Phone,
                                IsChild = p.IsChild,
                            };
                        })
                        .ToList(),
                };
                await _unitOfWork.Repository<DemoBooking>().AddAsync(newBooking, ct);

                await _auditLog.StageAsync(AuditAction.Create, nameof(DemoBooking), newBooking.Id.ToString(), cancellationToken: ct);
                await _unitOfWork.SaveChangesAsync(ct);

                return (Session: newSession, Booking: newBooking);
            }, cancellationToken);

            // Booking confirmation to the parent and every extra invitee (they may not
            // have accounts yet, so this bypasses the user-bound notification log)
            var jitsiConfigJson = await _unitOfWork.Repository<Integration>().Query()
                .Where(i => i.Key == "jitsi")
                .Select(i => i.ConfigJson)
                .FirstOrDefaultAsync(cancellationToken);
            var domain = JitsiLinkBuilder.ResolveDomain(jitsiConfigJson);

            // No account exists yet for a demo lead, so each invitee gets their own token
            // (name + email baked in, expiring a couple of hours past the demo) instead of
            // a bare room name that would work forever for anyone who ever saw the email.
            string JoinUrlFor(string participantName, string participantEmail) =>
                JitsiLinkBuilder.BuildJoinUrl(
                    session.MeetingRoomId,
                    jitsiConfigJson,
                    _jitsiTokenService.CreateToken(
                        domain, jitsiConfigJson, session.MeetingRoomId!, participantName, participantEmail,
                        moderator: false, request.ScheduledEndAtUtc.AddHours(2)))
                ?? "#";

            var (parentSubject, parentHtml) = await _emailTemplateService.RenderAsync(
                "demo-confirmed",
                new Dictionary<string, string>
                {
                    ["ChildName"] = booking.ChildName,
                    ["WhenLocal"] = $"{request.ScheduledStartAtUtc:u}",
                    ["JoinUrl"] = JoinUrlFor(booking.ParentName, booking.ParentEmail),
                },
                cancellationToken);
            await _emailSender.SendAsync(booking.ParentEmail, parentSubject, parentHtml, cancellationToken);
            foreach (var participant in booking.Participants.Where(p => !string.IsNullOrWhiteSpace(p.Email)))
            {
                var (participantSubject, participantHtml) = await _emailTemplateService.RenderAsync(
                    "demo-confirmed",
                    new Dictionary<string, string>
                    {
                        ["ChildName"] = booking.ChildName,
                        ["WhenLocal"] = $"{request.ScheduledStartAtUtc:u}",
                        ["JoinUrl"] = JoinUrlFor(participant.Name, participant.Email!),
                    },
                    cancellationToken);
                await _emailSender.SendAsync(participant.Email!, participantSubject, participantHtml, cancellationToken);
            }

            // New lead lands in the client's CRM (no-op when no webhook is configured)
            await _crmNotifier.PushLeadEventAsync("lead.created", new
            {
                booking.Id,
                booking.ParentName,
                booking.ParentEmail,
                booking.ParentPhone,
                booking.ChildName,
                Department = booking.Department?.ToString(),
                DemoAtUtc = request.ScheduledStartAtUtc,
            }, cancellationToken);

            return await GetAsync(booking.Id, cancellationToken);
        }

        /// <summary>
        /// Auto-assign: the department-matched active teacher who is free at the slot with the lightest day.
        /// </summary>
        /// <remarks>
        /// RACE FIXED (found 2026-08-09, fixed 2026-08-10) — but only because of where this is
        /// called from, so do not lift it out of that context. This reads "is anyone busy at
        /// this slot" and its caller inserts the session afterwards; under READ COMMITTED two
        /// concurrent requests for the same slot (reachable by any anonymous visitor via
        /// POST /api/store/demo-bookings) could both read "teacher free" and double-book the
        /// same teacher. There is no unique key to lean on here — the conflict is an overlap
        /// between arbitrary time ranges across rows, not a duplicate value — so the fix is at
        /// the isolation level: CreateAsync now runs this read and the matching insert inside
        /// one IUnitOfWork.ExecuteInSerializableTransactionAsync, where PostgreSQL's SSI tracks
        /// the predicate this query reads, spots the concurrent insert that invalidates it,
        /// aborts one side with SQLSTATE 40001, and the unit of work transparently retries it
        /// against the now-committed state (where this query correctly reports the teacher
        /// busy). Any future caller MUST keep this read and its insert inside the same
        /// serializable transaction; reading here and writing outside it silently restores the
        /// original bug.
        /// <para>
        /// Two honest limits on that guarantee. (1) SSI only sees other SERIALIZABLE
        /// transactions: a regular (non-demo) ClassSession committed concurrently by a code
        /// path that does not use ExecuteInSerializableTransactionAsync is not detected, so
        /// demo-vs-demo is now safe while demo-vs-concurrently-created-regular-session is not.
        /// (2) SQLite cannot reproduce the original race (one ADO.NET connection serializes
        /// every command) and this environment has no PostgreSQL, so the fix rests on SSI's
        /// documented semantics rather than on an observed concurrent run — see the scope note
        /// on Store_BookDemo_ConcurrentRequestsForSameSlot_MustNotDoubleBookTheOnlyTeacher for
        /// exactly what the tests do and do not prove.
        /// </para>
        /// </remarks>
        private async Task<Guid> AutoAssignTeacherAsync(CreateDemoBookingRequest request, CancellationToken cancellationToken)
        {
            IQueryable<TeacherProfile> teachers = _unitOfWork.Repository<TeacherProfile>().Query()
                .Where(t => t.User.Status == UserStatus.Active);
            if (request.Department.HasValue)
            {
                teachers = teachers.Where(t => t.Department == request.Department.Value);
            }

            var dayStart = request.ScheduledStartAtUtc.Date;
            var dayEnd = dayStart.AddDays(1);

            var candidates = await teachers
                .Select(t => new
                {
                    t.Id,
                    Busy = _unitOfWork.Repository<ClassSession>().Query().Any(
                        s => s.TeacherProfileId == t.Id
                             && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.CarriedForward)
                             && s.ScheduledStartAtUtc < request.ScheduledEndAtUtc
                             && s.ScheduledEndAtUtc > request.ScheduledStartAtUtc),
                    DayLoad = _unitOfWork.Repository<ClassSession>().Query().Count(
                        s => s.TeacherProfileId == t.Id
                             && s.ScheduledStartAtUtc >= dayStart
                             && s.ScheduledStartAtUtc < dayEnd),
                })
                .ToListAsync(cancellationToken);

            var chosen = candidates
                .Where(c => !c.Busy)
                .OrderBy(c => c.DayLoad)
                .FirstOrDefault()
                ?? throw new DomainValidationException("No teacher is available for this slot; pick a teacher or another time.");

            return chosen.Id;
        }

        public async Task<DemoBookingDto> UpdateConversionStatusAsync(
            Guid id,
            UpdateConversionStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            var booking = await _unitOfWork.Repository<DemoBooking>().GetByIdAsync(id, cancellationToken)
                ?? throw new NotFoundException(nameof(DemoBooking), id);

            booking.ConversionStatus = request.ConversionStatus;
            if (request.FollowUpNotes is not null)
            {
                booking.FollowUpNotes = request.FollowUpNotes;
            }

            await _auditLog.StageAsync(AuditAction.Update, nameof(DemoBooking), booking.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await _crmNotifier.PushLeadEventAsync("lead.status-changed", new
            {
                booking.Id,
                booking.ParentEmail,
                booking.ChildName,
                ConversionStatus = booking.ConversionStatus.ToString(),
                booking.FollowUpNotes,
            }, cancellationToken);

            return await GetAsync(booking.Id, cancellationToken);
        }

        public async Task<DemoFeedbackDto> SubmitFeedbackAsync(
            Guid demoBookingId,
            Guid teacherUserId,
            SubmitDemoFeedbackRequest request,
            CancellationToken cancellationToken = default)
        {
            var booking = await _unitOfWork.Repository<DemoBooking>().GetByIdAsync(demoBookingId, cancellationToken)
                ?? throw new NotFoundException(nameof(DemoBooking), demoBookingId);

            var teacher = await _unitOfWork.Repository<TeacherProfile>()
                .FirstOrDefaultAsync(t => t.UserId == teacherUserId, cancellationToken)
                ?? throw new NotFoundException("No teacher profile is linked to the current account.");

            var alreadySubmitted = await _unitOfWork.Repository<DemoFeedback>()
                .ExistsAsync(f => f.DemoBookingId == demoBookingId, cancellationToken);
            if (alreadySubmitted)
            {
                throw new DomainValidationException("Feedback has already been submitted for this demo.");
            }

            var feedback = new DemoFeedback
            {
                DemoBookingId = booking.Id,
                TeacherProfileId = teacher.Id,
                AcademicLevel = request.AcademicLevel.Trim(),
                Strengths = request.Strengths.Trim(),
                ImprovementAreas = request.ImprovementAreas.Trim(),
                RecommendedCourseId = request.RecommendedCourseId,
                SuggestedBatchType = request.SuggestedBatchType,
                Remarks = request.Remarks,
                SubmittedAtUtc = DateTime.UtcNow,
            };
            await _unitOfWork.Repository<DemoFeedback>().AddAsync(feedback, cancellationToken);

            // Feedback closes the demo stage; the booking enters the conversion pipeline
            if (booking.ConversionStatus == ConversionStatus.DemoScheduled)
            {
                booking.ConversionStatus = ConversionStatus.DemoCompleted;
            }

            await _auditLog.StageAsync(AuditAction.Create, nameof(DemoFeedback), feedback.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var saved = await FeedbackQuery().FirstAsync(f => f.Id == feedback.Id, cancellationToken);
            return ToFeedbackDto(saved);
        }

        public async Task<IReadOnlyList<DemoFeedbackDto>> ListFeedbackAsync(CancellationToken cancellationToken = default)
        {
            var feedbacks = await FeedbackQuery()
                .OrderByDescending(f => f.SubmittedAtUtc)
                .ToListAsync(cancellationToken);
            return feedbacks.Select(ToFeedbackDto).ToList();
        }

        public async Task<IReadOnlyList<DemoBookingDto>> ListForTeacherUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var teacher = await GetTeacherAsync(userId, cancellationToken);
            var bookings = await BaseQuery()
                .Where(b => b.ClassSession != null && b.ClassSession.TeacherProfileId == teacher.Id)
                .OrderByDescending(b => b.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            return bookings.Select(b => b.ToDto()).ToList();
        }

        public async Task<IReadOnlyList<DemoFeedbackDto>> ListFeedbackForTeacherUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var teacher = await GetTeacherAsync(userId, cancellationToken);
            var feedbacks = await FeedbackQuery()
                .Where(f => f.TeacherProfileId == teacher.Id)
                .OrderByDescending(f => f.SubmittedAtUtc)
                .ToListAsync(cancellationToken);
            return feedbacks.Select(ToFeedbackDto).ToList();
        }

        private async Task<TeacherProfile> GetTeacherAsync(Guid userId, CancellationToken cancellationToken)
        {
            return await _unitOfWork.Repository<TeacherProfile>()
                .FirstOrDefaultAsync(t => t.UserId == userId, cancellationToken)
                ?? throw new NotFoundException("No teacher profile is linked to the current account.");
        }

        private IQueryable<DemoFeedback> FeedbackQuery()
        {
            return _unitOfWork.Repository<DemoFeedback>().Query()
                .Include(f => f.DemoBooking)
                .Include(f => f.RecommendedCourse)
                .Include(f => f.TeacherProfile).ThenInclude(t => t.User);
        }

        private static DemoFeedbackDto ToFeedbackDto(DemoFeedback feedback)
        {
            return new DemoFeedbackDto
            {
                Id = feedback.Id,
                DemoBookingId = feedback.DemoBookingId,
                ChildName = feedback.DemoBooking.ChildName,
                ParentName = feedback.DemoBooking.ParentName,
                TeacherProfileId = feedback.TeacherProfileId,
                TeacherName = $"{feedback.TeacherProfile.User.FirstName} {feedback.TeacherProfile.User.LastName}",
                AcademicLevel = feedback.AcademicLevel,
                Strengths = feedback.Strengths,
                ImprovementAreas = feedback.ImprovementAreas,
                RecommendedCourseId = feedback.RecommendedCourseId,
                RecommendedCourseName = feedback.RecommendedCourse?.Name,
                SuggestedBatchType = feedback.SuggestedBatchType,
                Remarks = feedback.Remarks,
                SubmittedAtUtc = feedback.SubmittedAtUtc,
            };
        }

        public async Task<IReadOnlyList<ParentDemoHistoryDto>> ListParentHistoryAsync(
            string? search,
            CancellationToken cancellationToken = default)
        {
            var query = BaseQuery();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(b =>
                    b.ParentName.ToLower().Contains(term)
                    || b.ParentEmail.ToLower().Contains(term)
                    || (b.ParentPhone != null && b.ParentPhone.Contains(term)));
            }

            var bookings = await query.OrderByDescending(b => b.CreatedAtUtc).ToListAsync(cancellationToken);

            // One record per parent (email is the lead identity), every demo they've taken.
            return bookings
                .GroupBy(b => b.ParentEmail)
                .Select(g =>
                {
                    var dtos = g.Select(b => b.ToDto()).ToList();
                    return new ParentDemoHistoryDto
                    {
                        ParentEmail = g.Key,
                        ParentName = g.First().ParentName,
                        ParentPhone = g.First().ParentPhone,
                        TotalDemos = dtos.Count,
                        EnrolledCount = dtos.Count(d => d.ConversionStatus == ConversionStatus.Enrolled),
                        LastDemoAtUtc = dtos.Max(d => d.ScheduledStartAtUtc),
                        TotalPayable = dtos.Sum(d => d.PayableAmount),
                        Bookings = dtos,
                    };
                })
                .OrderByDescending(h => h.LastDemoAtUtc)
                .ToList();
        }

        private IQueryable<DemoBooking> BaseQuery()
        {
            return _unitOfWork.Repository<DemoBooking>().Query()
                .Include(b => b.ClassSession!).ThenInclude(s => s.TeacherProfile).ThenInclude(t => t.User)
                .Include(b => b.Participants);
        }
    }
}
