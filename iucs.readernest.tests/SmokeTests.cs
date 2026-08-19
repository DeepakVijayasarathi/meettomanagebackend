using iucs.readernest.application.Common.Exceptions;
using iucs.readernest.application.Dto.Academics;
using iucs.readernest.application.Dto.Admission;
using iucs.readernest.application.Dto.Auth;
using iucs.readernest.application.Dto.Batches;
using iucs.readernest.application.Dto.Billing;
using iucs.readernest.application.Dto.Communication;
using iucs.readernest.application.Dto.Courses;
using iucs.readernest.application.Dto.Enrollment;
using iucs.readernest.application.Dto.Integrations;
using iucs.readernest.application.Dto.Navigation;
using iucs.readernest.application.Dto.Payouts;
using iucs.readernest.application.Dto.Resources;
using iucs.readernest.application.Dto.Portal;
using iucs.readernest.application.Dto.Reports;
using iucs.readernest.application.Dto.Sessions;
using iucs.readernest.application.Dto.Settings;
using iucs.readernest.application.Dto.Users;
using iucs.readernest.application.Helper;
using iucs.readernest.application.Services;
using iucs.readernest.domain.Entities.Academics;
using iucs.readernest.domain.Entities.Admission;
using iucs.readernest.domain.Entities.Auditing;
using iucs.readernest.domain.Entities.Billing;
using iucs.readernest.domain.Entities.Communication;
using iucs.readernest.domain.Entities.Payouts;
using iucs.readernest.domain.Entities.Sessions;
using iucs.readernest.domain.Entities.Users;
using iucs.readernest.domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace iucs.readernest.tests
{
    public class SmokeTests : IDisposable
    {
        private readonly TestDatabase _db = new();
        private readonly BcryptPasswordHasher _hasher = new();
        private readonly FakeEmailSender _emailSender = new();
        private readonly AuditLogService _auditLog;
        private readonly EmailTemplateService _emailTemplates;
        private readonly NotificationService _notifications;

        public SmokeTests()
        {
            _auditLog = new AuditLogService(_db.UnitOfWork, _db.CurrentUser);
            _emailTemplates = new EmailTemplateService(_db.UnitOfWork, _auditLog, new MemoryCache(new MemoryCacheOptions()));
            _notifications = new NotificationService(_db.UnitOfWork, _emailSender, _emailTemplates, NullLogger<NotificationService>.Instance);
        }

        private AuthService CreateAuthService() =>
            new(_db.UnitOfWork, _hasher, new FakeTokenService(), _auditLog, _notifications, new ConfigurationBuilder().Build());

        private readonly FakeWhatsAppSender _whatsAppSender = new();

        private readonly FakeSmsSender _smsSender = new();

        private UserService CreateUserService() => new(_db.UnitOfWork, _hasher, _notifications, _emailTemplates, _auditLog, _emailSender, _whatsAppSender, _smsSender, NullLogger<UserService>.Instance);

        private CourseService CreateCourseService() => new(_db.UnitOfWork, _auditLog);

        private BatchService CreateBatchService() => new(_db.UnitOfWork, _auditLog, _notifications);

        private PayoutService CreatePayoutService() => new(_db.UnitOfWork, _auditLog, _notifications);

        private ProgressReportService CreateProgressReportService() => new(_db.UnitOfWork, _auditLog, _notifications);

        private StoreService CreateStoreService() => new(_db.UnitOfWork, _auditLog, CreateDemoBookingService());

        private SessionService CreateSessionService() => new(_db.UnitOfWork, _auditLog, CreatePayoutService(), _notifications, _db.CurrentUser, new FakeJitsiTokenService());

        private BillingService CreateBillingService() => new(_db.UnitOfWork, _auditLog, new FakePaymentGateway(), _notifications, _db.CurrentUser);

        private BillingService CreateBillingService(FakePaymentGateway gateway) => new(_db.UnitOfWork, _auditLog, gateway, _notifications, _db.CurrentUser);

        private EnrollmentService CreateEnrollmentService() => new(_db.UnitOfWork, _auditLog, CreateBillingService());

        private MenuService CreateMenuService() => new(_db.UnitOfWork, _auditLog);

        private AcademicOpsService CreateAcademicOpsService() =>
            new(_db.UnitOfWork, _auditLog, _notifications, _db.CurrentUser, CreateSessionService());

        private GamificationService CreateGamificationService() => new(_db.UnitOfWork, CreateSessionService());

        private ResourceService CreateResourceService() => new(_db.UnitOfWork, _auditLog);

        private DemoBookingService CreateDemoBookingService() =>
            new(_db.UnitOfWork, _auditLog, _emailSender, _emailTemplates, new FakeCrmNotifier(), new FakeJitsiTokenService(), NullLogger<DemoBookingService>.Instance);

        // ---- WBS business-rule coverage (Reader_Nest_LMS.pdf pp.28–32) ----

        [Fact]
        public async Task TeacherNoShow_AppliesDeduction_AndCarriesForward()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            await CreatePayoutService().SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 1000,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            var carried = await CreateSessionService().MarkNoShowAsync(
                session.Id, new MarkNoShowRequest { Party = NoShowParty.Teacher });

            var original = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.TeacherNoShow, original!.Status);
            Assert.Equal(SessionStatus.CarriedForward, (await _db.Context.ClassSessions.FindAsync(carried.Id))!.Status);
            var item = Assert.Single(_db.Context.PayoutItems.ToList());
            Assert.Equal(PayoutItemType.TeacherNoShowDeduction, item.Type);
            Assert.Equal(-1000m, item.Amount); // default penalty: 100% of the session rate
        }

        [Fact]
        public async Task GetJitsiJoin_RejectsAFarFutureOrAlreadyEndedSession_EvenForAValidParticipant()
        {
            // The UI's Join button only disables itself outside the window (parent/utils.ts
            // isJoinable: 10 min before start until the scheduled end) — confirmed live that
            // GET /api/sessions/{id}/jitsi-join itself had no equivalent check, so a real,
            // usable room + token was one direct request away regardless of what the button
            // showed, for a session weeks out or long over.
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var service = CreateSessionService();

            var farFuture = new ClassSession
            {
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(21),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(21).AddMinutes(45),
                Status = SessionStatus.Scheduled,
                MeetingRoomId = "trn-far-future",
            };
            var alreadyEnded = new ClassSession
            {
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(-1),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(45),
                Status = SessionStatus.Scheduled,
                MeetingRoomId = "trn-already-ended",
            };
            var withinWindow = new ClassSession
            {
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddMinutes(5),
                ScheduledEndAtUtc = DateTime.UtcNow.AddMinutes(50),
                Status = SessionStatus.Scheduled,
                MeetingRoomId = "trn-within-window",
            };
            _db.Context.AddRange(farFuture, alreadyEnded, withinWindow);
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.GetJitsiJoinAsync(farFuture.Id, teacherUser.Id));
            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.GetJitsiJoinAsync(alreadyEnded.Id, teacherUser.Id));

            var join = await service.GetJitsiJoinAsync(withinWindow.Id, teacherUser.Id);
            Assert.Equal("trn-within-window", join.Room);
        }

        [Fact]
        public async Task GetJitsiJoin_AllowsACoordinator_ButNotAPlainSubAdminWithoutTheGrant()
        {
            // coordinator/Calendar.tsx documents this as deliberate: "the coordinator can drop
            // into any ongoing/upcoming class or demo" — not scoped to a specific batch/session
            // the way Parent/Teacher access is, since coordinating means being able to check any
            // of them. Gated on the same SessionCalendarManagement:Edit grant the "coordinator"
            // preset carries, not the SubAdmin role generally — a Sub Admin without that specific
            // grant must still be refused.
            var otherTeacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = otherTeacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var session = new ClassSession
            {
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddMinutes(5),
                ScheduledEndAtUtc = DateTime.UtcNow.AddMinutes(50),
                Status = SessionStatus.Scheduled,
                MeetingRoomId = "trn-coordinator-check",
            };
            _db.Context.ClassSessions.Add(session);

            var coordinator = await _db.SeedUserAsync($"co-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            _db.Context.SubAdminPermissions.Add(new SubAdminPermission
            {
                UserId = coordinator.Id,
                Module = PermissionModule.SessionCalendarManagement,
                CanView = true,
                CanEdit = true,
            });

            var billingOnlySubAdmin = await _db.SeedUserAsync($"sa-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            _db.Context.SubAdminPermissions.Add(new SubAdminPermission
            {
                UserId = billingOnlySubAdmin.Id,
                Module = PermissionModule.BillingFinance,
                CanView = true,
            });
            await _db.Context.SaveChangesAsync();

            var service = CreateSessionService();
            var join = await service.GetJitsiJoinAsync(session.Id, coordinator.Id);
            Assert.Equal("trn-coordinator-check", join.Room);

            await Assert.ThrowsAsync<ForbiddenException>(
                () => service.GetJitsiJoinAsync(session.Id, billingOnlySubAdmin.Id));
        }

        [Fact]
        public async Task TeacherNoShow_AppliesConfiguredPenaltyPercent()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            await CreatePayoutService().SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 1000,
                TeacherNoShowPenaltyPercent = 150, // WBS p.31 "Penalty configuration"
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            await CreateSessionService().MarkNoShowAsync(
                session.Id, new MarkNoShowRequest { Party = NoShowParty.Teacher });

            var item = Assert.Single(_db.Context.PayoutItems.ToList());
            Assert.Equal(PayoutItemType.TeacherNoShowDeduction, item.Type);
            Assert.Equal(-1500m, item.Amount);
            Assert.Contains("150% of session rate", item.Note);
        }

        [Fact]
        public async Task DefaultRateCard_PaysTeachersWithoutOwnRates_AndTeacherRateOverridesIt()
        {
            var (batch, _, session) = await SeedBatchWithSessionAsync(totalSessions: 3);
            var payoutService = CreatePayoutService();

            // Only the centre-wide default card exists (TeacherProfileId = null)
            await payoutService.SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = null,
                DurationMinutes = 45,
                RatePerSession = 800,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            await CreateSessionService().CompleteAsync(session.Id);
            var defaultPaid = Assert.Single(_db.Context.PayoutItems.ToList());
            Assert.Equal(PayoutItemType.SessionEarning, defaultPaid.Type);
            Assert.Equal(800m, defaultPaid.Amount); // paid from the default card

            // The teacher's own rate takes precedence over the default from then on
            await payoutService.SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 1200,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            var secondStart = session.ScheduledStartAtUtc.AddDays(1);
            var second = new ClassSession
            {
                BatchId = batch.Id,
                TeacherProfileId = session.TeacherProfileId,
                Status = SessionStatus.Scheduled,
                ScheduledStartAtUtc = secondStart,
                ScheduledEndAtUtc = secondStart.AddMinutes(45),
            };
            _db.Context.ClassSessions.Add(second);
            await _db.Context.SaveChangesAsync();

            await CreateSessionService().CompleteAsync(second.Id);
            var overridden = _db.Context.PayoutItems.Single(i => i.ClassSessionId == second.Id);
            Assert.Equal(1200m, overridden.Amount);
        }

        [Fact]
        public async Task CompleteSession_RollsPayoutForward_WhenCurrentAndNextMonthAreBothAlreadyFinalized()
        {
            // Finance can finalize payroll before every session for the month is actually
            // done. This must never permanently block completing a late session — it used to
            // throw here when BOTH the session's own month and the next one were finalized.
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var period = new DateTime(session.ScheduledStartAtUtc.Year, session.ScheduledStartAtUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var next = period.AddMonths(1);
            _db.Context.Payouts.AddRange(
                new Payout { TeacherProfileId = session.TeacherProfileId, PeriodYear = period.Year, PeriodMonth = period.Month, Status = PayoutStatus.Finalized },
                new Payout { TeacherProfileId = session.TeacherProfileId, PeriodYear = next.Year, PeriodMonth = next.Month, Status = PayoutStatus.Finalized });
            await _db.Context.SaveChangesAsync();

            var completed = await CreateSessionService().CompleteAsync(session.Id); // must not throw

            Assert.Equal(SessionStatus.Completed, completed.Status);
            var item = await _db.Context.PayoutItems.Include(i => i.Payout).FirstAsync(i => i.ClassSessionId == session.Id);
            var rolledTo = period.AddMonths(2);
            Assert.Equal(rolledTo.Year, item.Payout.PeriodYear);
            Assert.Equal(rolledTo.Month, item.Payout.PeriodMonth);
            Assert.Equal(PayoutStatus.Pending, item.Payout.Status); // the new period, still open
        }

        [Fact]
        public async Task SubmitLeave_WithinSixHoursOfClass_IsBlocked()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();
            // A class starting in 2 hours — inside the 6-hour cutoff.
            var soon = DateTime.UtcNow.AddHours(2);
            _db.Context.ClassSessions.Add(new ClassSession
            {
                BatchId = (await _db.Context.Batches.FirstAsync()).Id,
                TeacherProfileId = teacher.Id,
                Status = SessionStatus.Scheduled,
                ScheduledStartAtUtc = soon,
                ScheduledEndAtUtc = soon.AddMinutes(45),
            });
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateAcademicOpsService().SubmitLeaveAsync(teacher.UserId, new SubmitLeaveRequest
                {
                    StartAtUtc = soon.AddMinutes(-30),
                    EndAtUtc = soon.AddHours(1),
                    Reason = "Sick",
                }));
        }

        [Fact]
        public async Task SubmitLeave_BeyondSixHours_Succeeds_AndAdminCanReject()
        {
            var (_, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();
            var ops = CreateAcademicOpsService();

            // Leave well beyond the 6-hour cutoff and clear of any class.
            var leave = await ops.SubmitLeaveAsync(teacher.UserId, new SubmitLeaveRequest
            {
                StartAtUtc = DateTime.UtcNow.AddDays(10),
                EndAtUtc = DateTime.UtcNow.AddDays(10).AddHours(2),
                Reason = "Family event",
            });
            Assert.Equal(LeaveStatus.Pending, leave.Status);

            // Simulate a fresh request/scope so the review re-loads cleanly (per-request context in prod).
            _db.Context.ChangeTracker.Clear();

            var reviewed = await ops.ReviewLeaveAsync(leave.Id, new ReviewLeaveRequest { Approve = false, ReviewNote = "Clash" });
            Assert.Equal(LeaveStatus.Rejected, reviewed.Status);
        }

        [Fact]
        public async Task CaptureAttendance_Rejoin_UpdatesRow_NeverDuplicates()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();
            var ops = CreateAcademicOpsService();

            await ops.CaptureAttendanceAsync(session.Id, new CaptureAttendanceRequest
            {
                Entries = [new AttendanceEntryDto { TeacherProfileId = teacher.Id, Status = AttendanceStatus.Present }],
            });
            // A network drop + rejoin sends the same participant again.
            await ops.CaptureAttendanceAsync(session.Id, new CaptureAttendanceRequest
            {
                Entries = [new AttendanceEntryDto { TeacherProfileId = teacher.Id, Status = AttendanceStatus.Late }],
            });

            var rows = _db.Context.SessionAttendances.Where(a => a.ClassSessionId == session.Id).ToList();
            Assert.Single(rows);
            Assert.Equal(AttendanceStatus.Late, rows[0].Status);
        }

        [Fact]
        public async Task CaptureAttendance_RejectsEntryWithBothChildAndTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateAcademicOpsService().CaptureAttendanceAsync(session.Id, new CaptureAttendanceRequest
                {
                    Entries = [new AttendanceEntryDto { ChildId = Guid.NewGuid(), TeacherProfileId = teacher.Id, Status = AttendanceStatus.Present }],
                }));
        }

        [Fact]
        public async Task AddRecording_SetsFifteenDayParentExpiry()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);

            var recording = await CreateSessionService().AddRecordingAsync(session.Id, new RegisterRecordingRequest
            {
                StorageUrl = "https://cdn.test/rec.mp4",
                DurationSeconds = 2700,
            });

            var stored = await _db.Context.SessionRecordings.FindAsync(recording.Id);
            Assert.NotNull(stored!.ExpiresAtUtc);
            var days = (stored.ExpiresAtUtc!.Value - DateTime.UtcNow).TotalDays;
            Assert.InRange(days, 14.9, 15.1);
        }

        [Fact]
        public async Task CreateInvoice_RoutesToMatchingDepartmentAccount_ByDefault()
        {
            var parentUser = await _db.SeedUserAsync($"dept-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var phonics = new PaymentAccount { Name = "Phonics", Department = Department.Phonics, GatewayProvider = "razorpay", GatewayAccountRef = "ph" };
            var maths = new PaymentAccount { Name = "Maths", Department = Department.Maths, GatewayProvider = "cashfree", GatewayAccountRef = "ma" };
            _db.Context.AddRange(parentProfile, phonics, maths);
            await _db.Context.SaveChangesAsync();

            var invoice = await CreateBillingService().CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Maths,
                Amount = 500,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var stored = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(maths.Id, stored.PaymentAccountId); // Maths course → Maths account
        }

        [Fact]
        public async Task ListInvoices_PagesNewestFirst_AndClampsAnOversizedPageSize()
        {
            var parentUser = await _db.SeedUserAsync($"page-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var phonics = new PaymentAccount { Name = "Phonics", Department = Department.Phonics, GatewayProvider = "razorpay", GatewayAccountRef = "ph" };
            _db.Context.AddRange(parentProfile, phonics);
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            for (var i = 0; i < 5; i++)
            {
                await billing.CreateInvoiceAsync(new CreateInvoiceRequest
                {
                    ParentProfileId = parentProfile.Id,
                    Department = Department.Phonics,
                    Amount = 100 + i,
                    DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                });
            }

            var first = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 1, pageSize: 2);
            Assert.Equal(5, first.TotalCount);
            Assert.Equal(2, first.Items.Count);
            Assert.Equal(1, first.Page);

            var second = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 2, pageSize: 2);
            Assert.Equal(2, second.Items.Count);
            // Pages must not overlap — IssuedAtUtc alone ties on rows created in the same tick,
            // so the ordering carries an Id tiebreaker to keep Skip/Take deterministic.
            Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));

            var third = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 3, pageSize: 2);
            Assert.Single(third.Items);

            // A caller asking for the whole table gets a bounded page back, not a table scan.
            var greedy = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 1, pageSize: 100_000);
            Assert.Equal(200, greedy.PageSize);
        }

        [Fact]
        public async Task AuditLog_ListAsync_PagesDeterministically_EvenWhenEntriesShareATimestamp()
        {
            // The audit interceptor stamps CreatedAtUtc once per SaveChanges call and applies
            // it to every entity in that batch, so adding all 5 rows in one AddRange +
            // SaveChangesAsync forces a genuine tie on the sort column — exactly the case
            // OrderByDescending(CreatedAtUtc) alone would resolve arbitrarily, letting
            // Skip/Take repeat or drop a row across pages.
            var actor = await _db.SeedUserAsync($"al-page-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var entries = Enumerable.Range(0, 5)
                .Select(i => new AuditLog { ActorUserId = actor.Id, Action = AuditAction.Update, EntityName = $"PagingTest-{i}" })
                .ToList();
            _db.Context.AuditLogs.AddRange(entries);
            await _db.Context.SaveChangesAsync();
            Assert.Single(entries.Select(e => e.CreatedAtUtc).Distinct()); // the tie really happened

            var first = await _auditLog.ListAsync(entityName: null, action: null, page: 1, pageSize: 2);
            var second = await _auditLog.ListAsync(entityName: null, action: null, page: 2, pageSize: 2);
            var third = await _auditLog.ListAsync(entityName: null, action: null, page: 3, pageSize: 2);

            Assert.Equal(2, first.Items.Count);
            Assert.Equal(2, second.Items.Count);
            var allIds = first.Items.Concat(second.Items).Concat(third.Items).Select(e => e.Id).ToList();
            Assert.Equal(allIds.Count, allIds.Distinct().Count()); // nothing repeated across pages
            Assert.True(allIds.Count >= 5); // and nothing from this batch is missing
        }

        [Fact]
        public async Task PartialThenFullPayment_TransitionsInvoiceStatus()
        {
            var parentUser = await _db.SeedUserAsync($"part-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var partial = await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 400 });
            Assert.Equal(InvoiceStatus.PartiallyPaid, partial.Status);

            var full = await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 600 });
            Assert.Equal(InvoiceStatus.Paid, full.Status);
        }

        [Fact]
        public async Task InlineCheckout_SettlesOnlyWithVerifiedSignature()
        {
            var parentUser = await _db.SeedUserAsync($"inline-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "razorpay", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var checkout = await billing.StartParentInlineCheckoutAsync(
                parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "razorpay" });
            Assert.Equal("inline", checkout.Mode);
            Assert.NotNull(checkout.OrderId);
            Assert.Equal(100_000, checkout.Amount); // minor units: 1000.00 → paise

            // A forged/failed signature must not settle the invoice.
            await Assert.ThrowsAsync<DomainValidationException>(() => billing.VerifyParentInlineCheckoutAsync(
                parentUser.Id, invoice.Id,
                new VerifyInlineCheckoutRequest { OrderId = checkout.OrderId!, PaymentId = "pay_1", Signature = "forged" }));
            Assert.Equal(InvoiceStatus.Pending, (await _db.Context.Invoices.FindAsync(invoice.Id))!.Status);

            // An order belonging to a different invoice must not settle this one either.
            await Assert.ThrowsAsync<NotFoundException>(() => billing.VerifyParentInlineCheckoutAsync(
                parentUser.Id, invoice.Id,
                new VerifyInlineCheckoutRequest { OrderId = "order_someone_elses", PaymentId = "pay_1", Signature = "valid" }));

            var settled = await billing.VerifyParentInlineCheckoutAsync(
                parentUser.Id, invoice.Id,
                new VerifyInlineCheckoutRequest { OrderId = checkout.OrderId!, PaymentId = "pay_1", Signature = "valid" });
            Assert.Equal(InvoiceStatus.Paid, settled.Status);
        }

        [Fact]
        public async Task FullPayment_AutoLiftsActiveFeeSuspension()
        {
            var parentUser = await _db.SeedUserAsync($"susp-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 800,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            });
            _db.Context.FeeSuspensions.Add(new FeeSuspension
            {
                ParentProfileId = parentProfile.Id, InvoiceId = invoice.Id,
                Status = SuspensionStatus.Active, SuspendedAtUtc = DateTime.UtcNow,
            });
            await _db.Context.SaveChangesAsync();

            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 800 });

            var suspension = await _db.Context.FeeSuspensions.FirstAsync(s => s.ParentProfileId == parentProfile.Id);
            Assert.Equal(SuspensionStatus.Lifted, suspension.Status);
            Assert.True(suspension.AutoRestored);
        }

        [Fact]
        public async Task FullPayment_DoesNotLiftSuspension_WhileAnotherInvoiceIsStillOverdue()
        {
            // A single suspension row can cover several overdue invoices at once
            // (BillingBackgroundService groups by parent) — paying off just one of them
            // must not restore access while another is still unpaid.
            var parentUser = await _db.SeedUserAsync($"multi-susp-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();

            var invoiceA = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 500,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
            });
            var invoiceB = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 300,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
            });
            // Simulates BillingBackgroundService's overdue sweep + suspension, without
            // running the background service itself.
            var trackedB = await _db.Context.Invoices.FirstAsync(i => i.Id == invoiceB.Id);
            trackedB.Status = InvoiceStatus.Overdue;
            _db.Context.FeeSuspensions.Add(new FeeSuspension
            {
                ParentProfileId = parentProfile.Id, InvoiceId = invoiceB.Id,
                Status = SuspensionStatus.Active, SuspendedAtUtc = DateTime.UtcNow,
            });
            await _db.Context.SaveChangesAsync();

            await billing.RecordPaymentAsync(invoiceA.Id, new RecordPaymentRequest { Amount = 500 });

            var paidA = await _db.Context.Invoices.FirstAsync(i => i.Id == invoiceA.Id);
            Assert.Equal(InvoiceStatus.Paid, paidA.Status);
            var suspension = await _db.Context.FeeSuspensions.FirstAsync(s => s.ParentProfileId == parentProfile.Id);
            Assert.Equal(SuspensionStatus.Active, suspension.Status); // invoice B is still overdue
        }

        [Fact]
        public async Task Refund_RequestThenApprove_IsRecorded()
        {
            var parentUser = await _db.SeedUserAsync($"ref-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });
            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });
            var txn = await _db.Context.PaymentTransactions.FirstAsync();

            var refund = await billing.RequestRefundAsync(new RequestRefundRequest
            {
                PaymentTransactionId = txn.Id, Amount = 250, Reason = "Partial goodwill",
            });
            var reviewed = await billing.ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true });

            Assert.Equal(RefundStatus.Processed, reviewed.Status);
            Assert.Equal(250, reviewed.Amount);
            // Persistence check (the AsNoTracking-mutation bug this caught): re-read from the DB.
            Assert.Equal(RefundStatus.Processed, (await _db.Context.Refunds.FirstAsync(r => r.Id == refund.Id)).Status);
        }

        /// <summary>A paid invoice with a Requested refund on its transaction, ready to review.</summary>
        private async Task<RefundDto> SeedRequestedRefundAsync(FakePaymentGateway gateway)
        {
            var parentUser = await _db.SeedUserAsync($"ref-race-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService(gateway);
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });
            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });
            var txn = await _db.Context.PaymentTransactions.FirstAsync(t => t.InvoiceId == invoice.Id);
            return await billing.RequestRefundAsync(new RequestRefundRequest
            {
                PaymentTransactionId = txn.Id, Amount = 250, Reason = "Race test",
            });
        }

        [Fact]
        public async Task Refund_ReviewedConcurrently_MustNotDoubleProcessTheSameRefund()
        {
            // SCOPE NOTE: SQLite cannot run two writers at once — both DbContexts here share one
            // SqliteConnection, which serializes command execution at the ADO.NET level — so this
            // test does not race anything. It instead *stages* the exact interleaving the race
            // depends on (request 2 reads "still Requested" before request 1 commits) and asserts
            // the fix rejects it. That is the whole substance of the bug: on Postgres the two
            // requests reach this state by timing, here they reach it by construction, and from
            // that state onwards the code path is identical. Against the pre-fix code this test
            // fails — request 2 would call the gateway a second time and refund the money twice.
            var gateway = new FakePaymentGateway();
            var refund = await SeedRequestedRefundAsync(gateway);

            // Request 2's own service graph on its own DbContext, exactly as ASP.NET Core would
            // hand a second concurrent caller (an admin double-click, or two admins working the
            // same queue). The shared FakePaymentGateway counts disbursements across both.
            var (context2, uow2) = _db.CreateConcurrentSession();
            var auditLog2 = new AuditLogService(uow2, _db.CurrentUser);
            var billing2 = new BillingService(uow2, auditLog2, gateway, _notifications, _db.CurrentUser);

            // Request 2 reads the refund while it is genuinely still Requested. EF returns this
            // same tracked instance from any later lookup on context2 rather than refreshing it,
            // so when ReviewRefundAsync runs below it sees precisely the stale "still Requested"
            // view a Postgres READ COMMITTED snapshot would have given it mid-race — sailing past
            // the in-memory status check and leaving the conditional UPDATE as the only guard.
            var staleRead = await uow2.Repository<Refund>().GetByIdAsync(refund.Id);
            Assert.Equal(RefundStatus.Requested, staleRead!.Status);

            // Request 1 wins the race and disburses.
            var reviewed = await CreateBillingService(gateway)
                .ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true });
            Assert.Equal(RefundStatus.Processed, reviewed.Status);
            Assert.Equal(1, gateway.RefundCallCount);

            // Request 2 now acts on its stale view. The UPDATE's WHERE clause no longer matches
            // (the row left Requested), so it affects 0 rows and the approval is refused before
            // the gateway is touched.
            var conflict = await Assert.ThrowsAsync<ConflictException>(
                () => billing2.ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true }));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal(RefundStatus.Requested, staleRead.Status); // it really was working off the stale read

            Assert.Equal(1, gateway.RefundCallCount); // the money moved exactly once

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var stored = await verifyContext.Refunds.FirstAsync(r => r.Id == refund.Id);
            Assert.Equal(RefundStatus.Processed, stored.Status);
            Assert.NotNull(stored.GatewayRefundId);

            context2.Dispose();
            verifyContext.Dispose();
        }

        [Fact]
        public async Task Refund_ApprovalFailingAtTheGateway_StaysClaimed_AndIsNotApprovableAgain()
        {
            // Fail-closed by design: the refund is claimed out of Requested BEFORE the gateway is
            // called, so a gateway error or timeout — where the disbursement may or may not have
            // actually happened — leaves the refund parked in Approved for an operator to
            // reconcile, never back in Requested where a retry could pay it a second time.
            var gateway = new FakePaymentGateway { RefundFailure = new TimeoutException("gateway timed out") };
            var refund = await SeedRequestedRefundAsync(gateway);

            await Assert.ThrowsAsync<TimeoutException>(
                () => CreateBillingService(gateway).ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true }));

            var (verifyContext, verifyUow) = _db.CreateConcurrentSession();
            var claimed = await verifyContext.Refunds.FirstAsync(r => r.Id == refund.Id);
            Assert.Equal(RefundStatus.Approved, claimed.Status); // claimed, not rolled back to Requested
            Assert.Null(claimed.GatewayRefundId);
            Assert.Null(claimed.ProcessedAtUtc);
            Assert.NotNull(claimed.UpdatedAtUtc); // ExecuteUpdate bypasses the interceptor — stamped by hand

            // A second approver (fresh context, so it reads the claimed row) is turned away
            // without the gateway being asked to refund again.
            gateway.RefundFailure = null;
            var auditLog2 = new AuditLogService(verifyUow, _db.CurrentUser);
            var billing2 = new BillingService(verifyUow, auditLog2, gateway, _notifications, _db.CurrentUser);
            var rejected = await Assert.ThrowsAsync<DomainValidationException>(
                () => billing2.ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true }));
            Assert.Contains("already Approved", rejected.Message);
            Assert.Equal(1, gateway.RefundCallCount);

            verifyContext.Dispose();
        }

        [Fact]
        public async Task Repository_ExecuteUpdate_MatchesOnlyRowsStillInTheExpectedState_AndStampsAuditFieldsByHand()
        {
            // The primitive the refund fix is built on, tested directly: the guard lives in the
            // UPDATE's WHERE clause, so a row that has already left the expected state matches 0
            // rows instead of being overwritten. Also pins the audit stamping, which the
            // SaveChanges interceptor cannot do for a change-tracker-bypassing bulk update.
            var actor = Guid.NewGuid();
            _db.CurrentUser.UserId = actor;
            var refund = await SeedRequestedRefundAsync(new FakePaymentGateway());
            var refunds = _db.UnitOfWork.Repository<Refund>();
            var stampedAt = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

            var won = await refunds.ExecuteUpdateAsync(
                r => r.Id == refund.Id && r.Status == RefundStatus.Requested,
                setters => setters
                    .SetProperty(r => r.Status, RefundStatus.Approved)
                    .SetProperty(r => r.UpdatedAtUtc, stampedAt)
                    .SetProperty(r => r.UpdatedBy, actor));
            Assert.Equal(1, won);

            // Same statement again: the row is no longer Requested, so nobody wins it twice.
            var lost = await refunds.ExecuteUpdateAsync(
                r => r.Id == refund.Id && r.Status == RefundStatus.Requested,
                setters => setters
                    .SetProperty(r => r.Status, RefundStatus.Rejected)
                    .SetProperty(r => r.UpdatedAtUtc, DateTime.UtcNow)
                    .SetProperty(r => r.UpdatedBy, actor));
            Assert.Equal(0, lost);

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var stored = await verifyContext.Refunds.FirstAsync(r => r.Id == refund.Id);
            Assert.Equal(RefundStatus.Approved, stored.Status); // the loser changed nothing
            Assert.Equal(stampedAt, stored.UpdatedAtUtc);
            Assert.Equal(actor, stored.UpdatedBy);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task ListInvoiceTransactions_ReportsAlreadyRefundedAndExcludesRejected()
        {
            var parentUser = await _db.SeedUserAsync($"txn-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });
            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });
            var txn = await _db.Context.PaymentTransactions.FirstAsync();

            // One rejected refund (must NOT count against the refundable balance) and one
            // still-pending request (must count) against the same transaction.
            var rejected = await billing.RequestRefundAsync(new RequestRefundRequest
            {
                PaymentTransactionId = txn.Id, Amount = 400, Reason = "Will be rejected",
            });
            await billing.ReviewRefundAsync(rejected.Id, new ReviewRefundRequest { Approve = false });
            await billing.RequestRefundAsync(new RequestRefundRequest
            {
                PaymentTransactionId = txn.Id, Amount = 300, Reason = "Still pending",
            });

            var rows = await billing.ListInvoiceTransactionsAsync(invoice.Id);

            var row = Assert.Single(rows);
            Assert.Equal(1000, row.Amount);
            Assert.Equal(300, row.AlreadyRefunded); // rejected 400 excluded, pending 300 included
        }

        [Fact]
        public async Task RenewSubscription_ReactivatesLapsedSubscription()
        {
            var parentUser = await _db.SeedUserAsync($"sub-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var plan = new PackagePlan { Name = "Monthly", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 2000 };
            // Starting/renewing a subscription now issues its first invoice immediately,
            // which routes through the department's payment account.
            var account = new PaymentAccount { Name = "Phonics", Department = Department.Phonics, GatewayProvider = "simulated", GatewayAccountRef = "ph" };
            _db.Context.AddRange(parentProfile, plan, account);
            await _db.Context.SaveChangesAsync();
            var child = new Child { ParentProfileId = parentProfile.Id, FirstName = "Kid", LastName = "One", IsActive = true };
            _db.Context.Children.Add(child);
            await _db.Context.SaveChangesAsync();
            var billing = CreateBillingService();
            var sub = await billing.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                ParentProfileId = parentProfile.Id, ChildId = child.Id, PackagePlanId = plan.Id,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            await billing.CancelSubscriptionAsync(sub.Id);

            var renewed = await billing.RenewSubscriptionAsync(sub.Id);
            Assert.Equal(SubscriptionStatus.Active, renewed.Status);
        }

        [Fact]
        public async Task ScheduleSession_OnHoliday_IsBlocked()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();
            var holiday = new DateOnly(2026, 8, 15);
            _db.Context.Holidays.Add(new Holiday { Name = "Independence Day", Date = holiday });
            await _db.Context.SaveChangesAsync();

            var start = holiday.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateSessionService().ScheduleAsync(new ScheduleSessionRequest
                {
                    BatchId = batch.Id,
                    TeacherProfileId = teacher.Id,
                    Type = SessionType.Regular,
                    ScheduledStartAtUtc = start,
                    ScheduledEndAtUtc = start.AddMinutes(45),
                }));
        }

        [Fact]
        public async Task ScheduleSession_RejectsAStartTimeInThePast()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();

            var start = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateSessionService().ScheduleAsync(new ScheduleSessionRequest
                {
                    BatchId = batch.Id,
                    TeacherProfileId = teacher.Id,
                    Type = SessionType.Regular,
                    ScheduledStartAtUtc = start,
                    ScheduledEndAtUtc = start.AddMinutes(45),
                }));
            Assert.Contains("cannot be in the past", ex.Message);

            var stored = await _db.Context.ClassSessions.CountAsync(s => s.BatchId == batch.Id);
            Assert.Equal(0, stored); // nothing was persisted
        }

        [Fact]
        public async Task RescheduleSession_RejectsAStartTimeInThePast()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var start = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc);

            var ex = await Assert.ThrowsAsync<DomainValidationException>(() => CreateSessionService().RescheduleAsync(
                session.Id,
                new RescheduleSessionRequest
                {
                    ScheduledStartAtUtc = start,
                    ScheduledEndAtUtc = start.AddMinutes(45),
                }));
            Assert.Contains("cannot be in the past", ex.Message);

            var untouched = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.Scheduled, untouched!.Status); // never rescheduled
        }

        [Fact]
        public async Task CreateHoliday_CarriesForwardClashingSessions()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var holidayDate = DateOnly.FromDateTime(session.ScheduledStartAtUtc);
            _db.Context.ChangeTracker.Clear();

            await CreateAcademicOpsService().CreateHolidayAsync(new SaveHolidayRequest
            {
                Name = "Surprise Holiday",
                Date = holidayDate,
            });

            var original = await _db.Context.ClassSessions.FirstAsync(s => s.Id == session.Id);
            Assert.Equal(SessionStatus.Cancelled, original.Status); // freed from the holiday
            var carried = await _db.Context.ClassSessions
                .FirstAsync(s => s.CarriedForwardFromSessionId == session.Id);
            Assert.Equal(SessionStatus.CarriedForward, carried.Status);
            Assert.Equal(session.ScheduledStartAtUtc.AddDays(7), carried.ScheduledStartAtUtc); // next available week
        }

        [Fact]
        public async Task FinalizeAndMarkPaid_PersistStatus_AndEmailSalarySlip()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var payoutService = CreatePayoutService();
            await payoutService.SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 900,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });
            await CreateSessionService().CompleteAsync(session.Id); // accrues the earning
            var payout = await _db.Context.Payouts.FirstAsync();
            _db.Context.ChangeTracker.Clear(); // fresh request scope

            await payoutService.FinalizeAsync(payout.Id);
            _db.Context.ChangeTracker.Clear();

            // Persistence check (the AsNoTracking-mutation bug this caught): re-read from the DB.
            var finalized = await _db.Context.Payouts.FirstAsync(p => p.Id == payout.Id);
            Assert.Equal(PayoutStatus.Finalized, finalized.Status);
            Assert.Equal(900, finalized.TotalAmount);
            _db.Context.ChangeTracker.Clear();

            await payoutService.MarkPaidAsync(payout.Id);
            _db.Context.ChangeTracker.Clear();
            var paid = await _db.Context.Payouts.FirstAsync(p => p.Id == payout.Id);
            Assert.Equal(PayoutStatus.Paid, paid.Status);

            // Salary slip auto-emailed on payment processing (client feedback #5)
            Assert.Contains(_emailSender.Sent, m => m.Subject.Contains("Salary slip"));
        }

        [Fact]
        public async Task ApproveLeave_Persists_AndNotifiesCoreTeamAndAffectedParents()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var teacher = await _db.Context.TeacherProfiles.FirstAsync();

            // A core-team RM + a parent with an actively enrolled child in the teacher's batch
            await _db.SeedUserAsync($"rm-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            var parentUser = await _db.SeedUserAsync($"lp-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "L", IsActive = true };
            _db.Context.AddRange(parentProfile, child,
                new BatchEnrollment { BatchId = batch.Id, Child = child, Status = EnrollmentStatus.Active });
            await _db.Context.SaveChangesAsync();

            var ops = CreateAcademicOpsService();
            var leave = await ops.SubmitLeaveAsync(teacher.UserId, new SubmitLeaveRequest
            {
                StartAtUtc = DateTime.UtcNow.AddDays(12),
                EndAtUtc = DateTime.UtcNow.AddDays(12).AddHours(3),
                Reason = "Conference",
            });
            _db.Context.ChangeTracker.Clear();
            _emailSender.Sent.Clear();

            await ops.ReviewLeaveAsync(leave.Id, new ReviewLeaveRequest { Approve = true });
            _db.Context.ChangeTracker.Clear();

            // Persistence check (the AsNoTracking-mutation bug this caught)
            var stored = await _db.Context.LeaveRequests.FirstAsync(l => l.Id == leave.Id);
            Assert.Equal(LeaveStatus.Approved, stored.Status);

            // Client feedback #10: core team + affected parents are notified
            Assert.Contains(_emailSender.Sent, m => m.Subject.StartsWith("Teacher on leave"));
            Assert.Contains(_emailSender.Sent, m => m.To == parentUser.Email && m.Subject.StartsWith("Class update"));
        }

        [Fact]
        public async Task Gamification_StarGrant_AutoAwardsMilestone_AtThreshold()
        {
            var gamification = CreateGamificationService();
            // A real session id — StudentAward.ClassSessionId is a FK.
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var sessionId = session.Id;
            // GrantAsync now requires genuine session participation — the session's own
            // assigned teacher is a valid caller.
            var callerId = (await _db.Context.TeacherProfiles.FindAsync(session.TeacherProfileId))!.UserId;

            await gamification.GrantAsync(callerId, new GrantAwardRequest { SessionId = sessionId, ParticipantName = "Aarav", Points = 2 });
            var afterTwo = await gamification.GetLeaderboardAsync(sessionId, 10);
            Assert.Equal(2, afterTwo.Single().Stars);
            Assert.Empty(afterTwo.Single().Badges);

            // Crossing 3 stars auto-grants the "Rising Star" milestone.
            var granted = await gamification.GrantAsync(callerId, new GrantAwardRequest { SessionId = sessionId, ParticipantName = "Aarav", Points = 1 });
            Assert.Contains(granted, a => a.Kind == AwardKind.Milestone);

            var afterThree = await gamification.GetLeaderboardAsync(sessionId, 10);
            Assert.Equal(3, afterThree.Single().Stars);
            Assert.NotEmpty(afterThree.Single().Badges);
        }

        [Fact]
        public async Task Menu_ForUser_FiltersItemsByRolePermission()
        {
            var subAdmin = await _db.SeedUserAsync($"menu-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            _db.Context.MenuItems.AddRange(
                new domain.Entities.Navigation.MenuItem
                {
                    Portal = "subadmin", Label = "Dashboard", Path = "/subadmin", Icon = "LayoutDashboard",
                    SectionOrder = 0, SortOrder = 0, IsActive = true, RequiredModule = null,
                },
                new domain.Entities.Navigation.MenuItem
                {
                    Portal = "subadmin", Label = "Billing", Path = "/subadmin/billing", Icon = "Receipt",
                    SectionOrder = 1, SortOrder = 0, IsActive = true, RequiredModule = PermissionModule.BillingFinance,
                });
            await _db.Context.SaveChangesAsync();

            var service = CreateMenuService();

            // Role grants no BillingFinance view → the gated item is hidden, the ungated one stays.
            var withoutBilling = await service.GetForUserAsync(subAdmin.Id, UserRole.SubAdmin, []);
            Assert.Contains(withoutBilling, m => m.Path == "/subadmin");
            Assert.DoesNotContain(withoutBilling, m => m.Path == "/subadmin/billing");

            // Grant BillingFinance view → the gated item appears.
            var withBilling = await service.GetForUserAsync(subAdmin.Id, UserRole.SubAdmin, [PermissionModule.BillingFinance]);
            Assert.Contains(withBilling, m => m.Path == "/subadmin/billing");

            // Admin bypasses the gate entirely.
            var adminUser = await _db.SeedUserAsync($"menuadmin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            _db.Context.MenuItems.Add(new domain.Entities.Navigation.MenuItem
            {
                Portal = "admin", Label = "Billing", Path = "/admin/billing", Icon = "Receipt",
                SectionOrder = 0, SortOrder = 0, IsActive = true, RequiredModule = PermissionModule.BillingFinance,
            });
            await _db.Context.SaveChangesAsync();
            var adminMenu = await service.GetForUserAsync(adminUser.Id, UserRole.Admin, []);
            Assert.Contains(adminMenu, m => m.Path == "/admin/billing");
        }

        [Fact]
        public async Task Login_Succeeds_WithValidCredentials()
        {
            await _db.SeedUserAsync("admin@test.com", _hasher.Hash("4821"), UserRole.Admin);

            var response = await CreateAuthService().LoginAsync(
                new LoginRequest { Email = "admin@test.com", Pin = "4821" });

            Assert.Equal("test-token", response.AccessToken);
            Assert.Equal(UserRole.Admin, response.User.Role);
        }

        [Fact]
        public async Task Login_Fails_WithWrongPin()
        {
            await _db.SeedUserAsync("admin@test.com", _hasher.Hash("4821"), UserRole.Admin);

            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                CreateAuthService().LoginAsync(new LoginRequest { Email = "admin@test.com", Pin = "0000" }));
        }

        [Fact]
        public async Task GetCurrentAccess_ReflectsAPermissionRevokedAfterLogin_WithoutANewLogin()
        {
            // Backs Program.cs's OnTokenValidated: a permission grant baked into a JWT at login
            // used to stay valid for that token's whole lifetime even after being revoked. This
            // proves the underlying read this fix relies on is genuinely live — same UnitOfWork
            // session throughout, no re-login, no new token — the way a real request's DB round
            // trip would see it after the app itself changed the same rows out from under it.
            var subAdminUser = await _db.SeedUserAsync($"sub-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            var otherAdmin = await _db.SeedUserAsync($"admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            _db.Context.SubAdminPermissions.Add(new SubAdminPermission
            {
                UserId = subAdminUser.Id,
                Module = PermissionModule.BillingFinance,
                CanView = true,
            });
            await _db.Context.SaveChangesAsync();
            // UserService.SetPermissionsAsync below re-queries and removes this same row;
            // production hands each request its own DbContext, so the seed instance must not
            // stay tracked here (mirrors the same pattern used for RoleService tests).
            _db.Context.ChangeTracker.Clear();

            var auth = CreateAuthService();
            var beforeRevoke = await auth.GetCurrentAccessAsync(subAdminUser.Id);
            Assert.Contains($"{PermissionModule.BillingFinance}:{PermissionAction.View}", beforeRevoke!.Permissions);

            // Revoke it — the same "replace-all" path the real Permissions screen uses.
            await CreateUserService().SetPermissionsAsync(subAdminUser.Id, otherAdmin.Id, []);

            var afterRevoke = await auth.GetCurrentAccessAsync(subAdminUser.Id);
            Assert.DoesNotContain($"{PermissionModule.BillingFinance}:{PermissionAction.View}", afterRevoke!.Permissions);
        }

        [Fact]
        public async Task Login_Blocks_InactiveUser()
        {
            await _db.SeedUserAsync("gone@test.com", _hasher.Hash("4821"), status: UserStatus.Inactive);

            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                CreateAuthService().LoginAsync(new LoginRequest { Email = "gone@test.com", Pin = "4821" }));
        }

        [Fact]
        public async Task RequestPinReset_ThenResetPin_ChangesPinAndBurnsToken()
        {
            var user = await _db.SeedUserAsync("reset@test.com", _hasher.Hash("1234"));
            var auth = CreateAuthService();

            await auth.RequestPinResetAsync(new ForgotPinRequest { Email = "reset@test.com" });

            var token = await _db.Context.PinResetTokens.FirstAsync(t => t.UserId == user.Id);
            Assert.Null(token.UsedAtUtc);
            Assert.Single(_emailSender.Sent); // the reset link actually went out

            await auth.ResetPinAsync(new ResetPinRequest { Token = token.Token, NewPin = "9999" });

            var reloaded = await _db.Context.Users.FirstAsync(u => u.Id == user.Id);
            Assert.True(_hasher.Verify("9999", reloaded.PinHash));
            Assert.False(_hasher.Verify("1234", reloaded.PinHash)); // old PIN no longer works
            var burnedToken = await _db.Context.PinResetTokens.FirstAsync(t => t.Id == token.Id);
            Assert.NotNull(burnedToken.UsedAtUtc);

            // Single-use: redeeming the same token again must fail, not silently reset again.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                auth.ResetPinAsync(new ResetPinRequest { Token = token.Token, NewPin = "0000" }));
        }

        [Fact]
        public async Task RequestPinReset_UnknownEmail_DoesNothingAndNeverThrows()
        {
            var auth = CreateAuthService();

            // No account with this email exists — must complete quietly (no enumeration signal),
            // never throw NotFoundException or anything else the caller could distinguish.
            await auth.RequestPinResetAsync(new ForgotPinRequest { Email = "nobody@test.com" });

            Assert.Empty(await _db.Context.PinResetTokens.ToListAsync());
            Assert.Empty(_emailSender.Sent);
        }

        [Fact]
        public async Task ResetPin_ExpiredToken_ThrowsAndLeavesPinUnchanged()
        {
            var user = await _db.SeedUserAsync("expired@test.com", _hasher.Hash("1234"));
            _db.Context.PinResetTokens.Add(new PinResetToken
            {
                UserId = user.Id,
                Token = "expired-token-value",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), // already expired
            });
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateAuthService().ResetPinAsync(new ResetPinRequest { Token = "expired-token-value", NewPin = "5555" }));

            var reloaded = await _db.Context.Users.FirstAsync(u => u.Id == user.Id);
            Assert.True(_hasher.Verify("1234", reloaded.PinHash)); // unchanged
        }

        [Fact]
        public async Task CreateUser_Parent_CreatesProfile_AndEmailsCredentials()
        {
            var dto = await CreateUserService().CreateAsync(new CreateUserRequest
            {
                Email = "Parent@Example.com",
                FirstName = "Rhea",
                LastName = "Kapoor",
                Role = UserRole.Parent,
            });

            Assert.Equal("parent@example.com", dto.Email);
            Assert.Single(_db.Context.ParentProfiles);
            var email = Assert.Single(_emailSender.Sent);
            Assert.Contains("PIN", email.Body);
        }

        [Fact]
        public async Task ListUsers_SkipsARowWithACorruptStoredEnumValue_InsteadOfFailingTheWholePage()
        {
            // Every enum column round-trips as a string; this simulates the real production
            // failure — a users.role value that doesn't match any current UserRole member
            // (stale data, a manual edit, whatever) — by writing one directly, bypassing EF's
            // type-safe API entirely, the same way corrupt data actually gets into a real
            // database.
            var good1 = await _db.SeedUserAsync("good1@test.com", "x", UserRole.Teacher);
            var corrupt = await _db.SeedUserAsync("corrupt@test.com", "x", UserRole.Teacher);
            var good2 = await _db.SeedUserAsync("good2@test.com", "x", UserRole.Teacher);

            await _db.Context.Database.ExecuteSqlRawAsync(
                "UPDATE users SET role = 'NotARealRole' WHERE id = {0}", corrupt.Id);

            var service = CreateUserService();
            var page = await service.ListAsync(role: null, search: null, page: 1, pageSize: 100);

            // The corrupt row is skipped, not defaulted to some guessed role — but it doesn't
            // take the other two rows down with it the way a single ToListAsync() over the
            // whole batch used to.
            Assert.Contains(page.Items, u => u.Id == good1.Id);
            Assert.Contains(page.Items, u => u.Id == good2.Id);
            Assert.DoesNotContain(page.Items, u => u.Id == corrupt.Id);
        }

        [Fact]
        public async Task CreateUser_DuplicateEmail_Throws()
        {
            var service = CreateUserService();
            var request = new CreateUserRequest
            {
                Email = "dup@test.com",
                FirstName = "A",
                LastName = "B",
                Role = UserRole.Teacher,
            };
            await service.CreateAsync(request);

            await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(request));
        }

        [Fact]
        public async Task DeleteUser_SoftDeletes_AndFreesUpEmailForReuse()
        {
            var service = CreateUserService();
            var teacherUser = await _db.SeedUserAsync($"del-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var email = teacherUser.Email;
            var adminUser = await _db.SeedUserAsync($"admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);

            await service.DeleteAsync(teacherUser.Id, adminUser.Id);

            await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(teacherUser.Id));

            // The email should be free again since the query filter excludes soft-deleted rows.
            var dto = await service.CreateAsync(new CreateUserRequest
            {
                Email = email,
                FirstName = "New",
                LastName = "Teacher",
                Role = UserRole.Teacher,
            });
            Assert.Equal(email.ToLowerInvariant(), dto.Email);
        }

        [Fact]
        public async Task DeleteUser_RefusesSelfDelete_AndLastAdmin()
        {
            var service = CreateUserService();
            var admin = await _db.SeedUserAsync($"solo-admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var otherAdmin = await _db.SeedUserAsync($"other-admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var subAdmin = await _db.SeedUserAsync($"sa-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);

            await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(admin.Id, admin.Id));

            // Two admins exist — deleting one is fine.
            await service.DeleteAsync(otherAdmin.Id, admin.Id);

            // Now only one admin remains — deleting it is blocked.
            await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(admin.Id, subAdmin.Id));
        }

        [Fact]
        public async Task ChangeUserRole_SwapsProfile_WhenNoOperationalHistory()
        {
            var service = CreateUserService();
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = parentUser.Id });
            await _db.Context.SaveChangesAsync();

            var dto = await service.ChangeRoleAsync(parentUser.Id, UserRole.Teacher);

            Assert.Equal(UserRole.Teacher, dto.Role);
            Assert.False(await _db.Context.ParentProfiles.AnyAsync(p => p.UserId == parentUser.Id));
            Assert.True(await _db.Context.TeacherProfiles.AnyAsync(t => t.UserId == parentUser.Id));
        }

        [Fact]
        public async Task ChangeUserRole_RefusesParentWithChildren_AndTeacherWithSessions()
        {
            var service = CreateUserService();

            var (batch, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var teacherUserId = session.TeacherProfileId;
            var teacherUser = await _db.Context.TeacherProfiles.Where(t => t.Id == teacherUserId).Select(t => t.UserId).FirstAsync();
            await Assert.ThrowsAsync<ConflictException>(() => service.ChangeRoleAsync(teacherUser, UserRole.AdmissionTeam));

            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            await _db.Context.SaveChangesAsync();
            _db.Context.Children.Add(new Child { ParentProfileId = parentProfile.Id, FirstName = "Kid", LastName = "Test" });
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<ConflictException>(() => service.ChangeRoleAsync(parentUser.Id, UserRole.Teacher));
        }

        [Fact]
        public async Task ChangeUserRole_RefusesAdminAsSourceOrTarget()
        {
            var service = CreateUserService();
            var admin = await _db.SeedUserAsync($"admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = parentUser.Id });
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() => service.ChangeRoleAsync(admin.Id, UserRole.Teacher));
            await Assert.ThrowsAsync<DomainValidationException>(() => service.ChangeRoleAsync(parentUser.Id, UserRole.Admin));
        }

        [Fact]
        public async Task ParentSchedule_IncludesDemoSession_ForLeadWithNoEnrolledChildYet()
        {
            var parentEmail = $"lead-{Guid.NewGuid():N}@test.com";
            var parentUser = await _db.SeedUserAsync(parentEmail, "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = parentUser.Id });

            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var demoStart = DateTime.UtcNow.AddDays(1);
            var demoSession = new ClassSession
            {
                BatchId = null,
                TeacherProfile = teacher,
                Type = SessionType.Demo,
                Status = SessionStatus.Scheduled,
                ScheduledStartAtUtc = demoStart,
                ScheduledEndAtUtc = demoStart.AddMinutes(30),
            };
            _db.Context.ClassSessions.Add(demoSession);
            _db.Context.DemoBookings.Add(new DemoBooking
            {
                ClassSession = demoSession,
                ParentName = "Lead Parent",
                ParentEmail = parentEmail,
                ChildName = "Prospective Kid",
            });
            await _db.Context.SaveChangesAsync();

            var service = new ParentPortalService(_db.UnitOfWork);
            var schedule = await service.GetScheduleAsync(
                parentUser.Id, demoStart.AddDays(-1), demoStart.AddDays(2));

            var found = Assert.Single(schedule);
            Assert.Equal(demoSession.Id, found.Id);
            Assert.Equal(SessionType.Demo, found.Type);
        }

        [Fact]
        public async Task CreateDemoBooking_ConfirmationEmail_IncludesJitsiJoinLink()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var dto = await CreateDemoBookingService().CreateAsync(new CreateDemoBookingRequest
            {
                ParentName = "Lead Parent",
                ParentEmail = "lead@test.com",
                ChildName = "Kid",
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(1),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            });

            var email = Assert.Single(_emailSender.Sent, e => e.To == "lead@test.com");
            Assert.Contains($"https://meet.techmisai.com/{dto.MeetingRoomId}", email.Body);
        }

        [Fact]
        public async Task CreateDemoBooking_SucceedsAndReturnsTheBooking_EvenWhenConfirmationEmailFails()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var service = new DemoBookingService(
                _db.UnitOfWork, _auditLog, new ThrowingEmailSender(), _emailTemplates,
                new FakeCrmNotifier(), new FakeJitsiTokenService(), NullLogger<DemoBookingService>.Instance);

            // An SMTP failure (confirmed in production logs as an uncaught exception here) must
            // not turn an already-committed booking into a 500 — the booking is real by the time
            // this code runs, and a delivery failure shouldn't undo confirming that to the caller.
            var dto = await service.CreateAsync(new CreateDemoBookingRequest
            {
                ParentName = "Lead Parent",
                ParentEmail = "lead2@test.com",
                ChildName = "Kid",
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(1),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            });

            Assert.NotEqual(Guid.Empty, dto.Id);
            Assert.NotNull(await _db.Context.DemoBookings.FirstOrDefaultAsync(b => b.Id == dto.Id));
        }

        [Fact]
        public async Task CreateDemoBooking_RejectsExplicitTeacher_AlreadyBookedAtThatTime()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            _db.Context.TeacherProfiles.Add(teacher);
            await _db.Context.SaveChangesAsync();

            var start = DateTime.UtcNow.AddDays(1);
            var end = start.AddMinutes(30);
            var service = CreateDemoBookingService();

            await service.CreateAsync(new CreateDemoBookingRequest
            {
                ParentName = "First Parent",
                ParentEmail = "first@test.com",
                ChildName = "Kid One",
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = start,
                ScheduledEndAtUtc = end,
            });

            // Same teacher, overlapping slot, explicitly requested this time instead of
            // auto-assigned — must be rejected the same way auto-assign already avoids it.
            await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(new CreateDemoBookingRequest
            {
                ParentName = "Second Parent",
                ParentEmail = "second@test.com",
                ChildName = "Kid Two",
                TeacherProfileId = teacher.Id,
                ScheduledStartAtUtc = start.AddMinutes(10),
                ScheduledEndAtUtc = end.AddMinutes(10),
            }));

            Assert.Equal(1, await _db.Context.DemoBookings.CountAsync(b => b.ChildName == "Kid One" || b.ChildName == "Kid Two"));
        }

        [Fact]
        public void JitsiLinkBuilder_UsesConfiguredDomain_WhenIntegrationConfigured()
        {
            var url = JitsiLinkBuilder.BuildJoinUrl("trn-abc123", """{"domain":"meet.example.org"}""");
            Assert.Equal("https://meet.example.org/trn-abc123", url);
        }

        [Fact]
        public void JitsiLinkBuilder_FallsBackToDefaultDomain_WhenConfigMissingOrMalformed()
        {
            Assert.Equal("https://meet.techmisai.com/trn-abc123", JitsiLinkBuilder.BuildJoinUrl("trn-abc123", null));
            Assert.Equal("https://meet.techmisai.com/trn-abc123", JitsiLinkBuilder.BuildJoinUrl("trn-abc123", "not-json"));
        }

        [Fact]
        public void JitsiLinkBuilder_ReturnsNull_WhenNoMeetingRoom()
        {
            Assert.Null(JitsiLinkBuilder.BuildJoinUrl(null, """{"domain":"meet.example.org"}"""));
        }

        [Fact]
        public async Task CreateCourse_RejectsInvalidDuration()
        {
            var courseService = CreateCourseService();
            var category = await courseService.CreateCategoryAsync(
                new CreateCourseCategoryRequest { Name = "Phonics", Department = Department.Phonics });

            await Assert.ThrowsAsync<DomainValidationException>(() => courseService.CreateAsync(new SaveCourseRequest
            {
                CourseCategoryId = category.Id,
                Name = "Bad",
                Type = CourseType.Group,
                DurationMinutes = 50,
                Price = 1,
                TotalSessions = 1,
                Department = Department.Phonics,
            }));
        }

        [Fact]
        public async Task Reschedule_LinksReplacementToOriginal_AndMarksOriginal()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var sessionService = CreateSessionService();

            var replacement = await sessionService.RescheduleAsync(session.Id, new RescheduleSessionRequest
            {
                ScheduledStartAtUtc = session.ScheduledStartAtUtc.AddDays(1),
                ScheduledEndAtUtc = session.ScheduledEndAtUtc.AddDays(1),
            });

            Assert.Equal(session.Id, replacement.RescheduledFromSessionId);
            var original = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.Rescheduled, original!.Status);
        }

        [Fact]
        public async Task Reschedule_RejectsHolidayDate()
        {
            // ScheduleAsync already blocked holidays; RescheduleAsync didn't — a reschedule
            // is a new calendar entry too, and was the one path that could land a class on one.
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var holidayDate = DateOnly.FromDateTime(session.ScheduledStartAtUtc.AddDays(3));
            _db.Context.Holidays.Add(new Holiday { Name = "Founders' Day", Date = holidayDate });
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() => CreateSessionService().RescheduleAsync(
                session.Id,
                new RescheduleSessionRequest
                {
                    ScheduledStartAtUtc = holidayDate.ToDateTime(TimeOnly.FromDateTime(session.ScheduledStartAtUtc)),
                    ScheduledEndAtUtc = holidayDate.ToDateTime(TimeOnly.FromDateTime(session.ScheduledEndAtUtc)),
                }));

            var untouched = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.Scheduled, untouched!.Status); // never rescheduled
        }

        [Fact]
        public async Task MarkNoShow_CarriedForwardSession_SkipsAHolidayOneWeekOut()
        {
            // The naive "+7 days" placement used to ignore the holiday calendar entirely.
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var oneWeekOut = DateOnly.FromDateTime(session.ScheduledStartAtUtc.AddDays(7));
            _db.Context.Holidays.Add(new Holiday { Name = "Regional Holiday", Date = oneWeekOut });
            await _db.Context.SaveChangesAsync();

            var carried = await CreateSessionService().MarkNoShowAsync(
                session.Id, new MarkNoShowRequest { Party = NoShowParty.Student });

            Assert.Equal(SessionStatus.CarriedForward, carried.Status);
            Assert.NotEqual(oneWeekOut, DateOnly.FromDateTime(carried.ScheduledStartAtUtc));
        }

        [Fact]
        public async Task CompleteSession_MovesBatchToDormant_WhenCourseFinishes()
        {
            var (batch, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);

            await CreateSessionService().CompleteAsync(session.Id);

            var reloaded = await _db.Context.Batches.FindAsync(batch.Id);
            Assert.Equal(BatchStatus.Dormant, reloaded!.Status);
            Assert.NotNull(reloaded.CompletedAtUtc);
        }

        [Fact]
        public async Task RecordPayment_MarksInvoicePaid_AndGeneratesReceipt()
        {
            var parentUser = await _db.SeedUserAsync("p@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Phonics",
                Department = Department.Phonics,
                GatewayProvider = "test",
                GatewayAccountRef = "acc-1",
            });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Phonics,
                Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var paid = await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });

            Assert.Equal(InvoiceStatus.Paid, paid.Status);
            var transaction = Assert.Single(_db.Context.PaymentTransactions.ToList());
            Assert.StartsWith("RCP-", transaction.ReceiptNumber);
        }

        [Fact]
        public async Task CreateInvoice_RoutesThroughParentAccountOverride_WhenSet()
        {
            var parentUser = await _db.SeedUserAsync($"map-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var phonics = new PaymentAccount { Name = "Phonics", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "ph" };
            var maths = new PaymentAccount { Name = "Maths", Department = Department.Maths, GatewayProvider = "t", GatewayAccountRef = "ma" };
            // Parent is pinned to the Maths account even though the invoice is a Phonics one.
            var parentProfile = new ParentProfile { UserId = parentUser.Id, PaymentAccount = maths };
            _db.Context.AddRange(phonics, maths, parentProfile);
            await _db.Context.SaveChangesAsync();

            var invoice = await CreateBillingService().CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Phonics,
                Amount = 500,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var stored = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(maths.Id, stored.PaymentAccountId); // override wins over the department account
            Assert.Equal(Department.Phonics, stored.Department); // department still reflects the course
        }

        [Fact]
        public async Task CreatePaymentLink_ReturnsShareableUrl_ForOpenInvoice()
        {
            var parentUser = await _db.SeedUserAsync("link@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Maths",
                Department = Department.Maths,
                GatewayProvider = "test",
                GatewayAccountRef = "acc-2",
            });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Maths,
                Amount = 2500,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var link = await billing.CreatePaymentLinkAsync(invoice.Id);

            Assert.Contains(invoice.Id.ToString(), link.Url);
            Assert.Equal(2500, link.AmountDue);
            Assert.StartsWith("TEST-", link.GatewayReference);
        }

        [Fact]
        public async Task ParentPayNow_GatewayCheckout_SettlesViaWebhook_Idempotently()
        {
            var parentUser = await _db.SeedUserAsync($"paynow-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Phonics",
                Department = Department.Phonics,
                GatewayProvider = "razorpay",
                GatewayAccountRef = "acc-1",
            });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Phonics,
                Amount = 800,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            // Parent initiates checkout: a pending transaction carries the link reference
            var result = await billing.InitiateParentPaymentAsync(
                parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "razorpay" });

            Assert.Equal("redirect", result.Mode);
            Assert.NotNull(result.Url);
            var pending = await _db.Context.PaymentTransactions
                .SingleAsync(t => t.GatewayTransactionId == result.GatewayReference);
            Assert.Equal(TransactionStatus.Pending, pending.Status);

            // Webhook settles the reference; a retry of the same event is a no-op
            await billing.SettleGatewayTransactionAsync(result.GatewayReference!, true, "pay_123", null);
            await billing.SettleGatewayTransactionAsync(result.GatewayReference!, true, "pay_123", null);

            var storedInvoice = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Paid, storedInvoice.Status);
            Assert.Equal(800, storedInvoice.AmountPaid);
            var settled = await _db.Context.PaymentTransactions.SingleAsync(t => t.InvoiceId == invoice.Id);
            Assert.Equal(TransactionStatus.Success, settled.Status);
            Assert.StartsWith("RCP-", settled.ReceiptNumber);
            Assert.Contains("pay_123", settled.GatewayTransactionId);
        }

        [Fact]
        public async Task GatewaySettlement_CapsAtRemainingBalance_WhenAnotherPaymentAlreadyLanded()
        {
            // A parallel checkout attempt and a manual cash payment can both be in flight on
            // the same invoice; the gateway's late webhook must never push AmountPaid past
            // Amount just because the transaction it's settling was created for the full price.
            var parentUser = await _db.SeedUserAsync($"overpay-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Phonics", Department = Department.Phonics, GatewayProvider = "razorpay", GatewayAccountRef = "acc-1",
            });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            // Gateway checkout starts for the full amount...
            var checkout = await billing.InitiateParentPaymentAsync(
                parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "razorpay" });

            // ...but a cash payment covers part of the balance before the webhook arrives.
            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 400 });
            Assert.Equal(InvoiceStatus.PartiallyPaid, (await _db.Context.Invoices.FindAsync(invoice.Id))!.Status);

            // The gateway now confirms the ORIGINAL full-amount transaction.
            await billing.SettleGatewayTransactionAsync(checkout.GatewayReference!, true, "pay_late", null);

            var settledInvoice = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Paid, settledInvoice.Status);
            Assert.Equal(1000, settledInvoice.AmountPaid); // capped, not 400 + 1000 = 1400
        }

        [Fact]
        public async Task ReconcileInvoicePayment_SettlesFromGatewayStatus_WithoutWebhook()
        {
            var parentUser = await _db.SeedUserAsync($"reconcile-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Phonics",
                Department = Department.Phonics,
                GatewayProvider = "razorpay",
                GatewayAccountRef = "acc-1",
            });
            await _db.Context.SaveChangesAsync();

            var gateway = new FakePaymentGateway();
            var billing = CreateBillingService(gateway);
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Phonics,
                Amount = 950,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var result = await billing.InitiateParentPaymentAsync(
                parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "razorpay" });

            // Before reconcile: the invoice is still unpaid (no webhook arrived).
            var before = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.NotEqual(InvoiceStatus.Paid, before.Status);

            // The gateway now reports the link paid; a pull-based reconcile settles it.
            gateway.PaidReferences.Add(result.GatewayReference!);
            _db.Context.ChangeTracker.Clear();

            var refreshed = await billing.ReconcileInvoicePaymentAsync(parentUser.Id, invoice.Id);

            Assert.Equal(InvoiceStatus.Paid, refreshed.Status);
            var storedInvoice = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatus.Paid, storedInvoice.Status);
            Assert.Equal(950, storedInvoice.AmountPaid);
            var settled = await _db.Context.PaymentTransactions.SingleAsync(t => t.InvoiceId == invoice.Id);
            Assert.Equal(TransactionStatus.Success, settled.Status);
            Assert.StartsWith("RCP-", settled.ReceiptNumber);
        }

        [Fact]
        public async Task ParentPayNow_Cash_RecordsPendingIntent_WithoutTouchingInvoice()
        {
            var parentUser = await _db.SeedUserAsync($"cash-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            _db.Context.PaymentAccounts.Add(new PaymentAccount
            {
                Name = "Maths",
                Department = Department.Maths,
                GatewayProvider = "cashfree",
                GatewayAccountRef = "acc-2",
            });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id,
                Department = Department.Maths,
                Amount = 1200,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            var result = await billing.InitiateParentPaymentAsync(
                parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "cash" });

            Assert.Equal("cash", result.Mode);
            var intent = await _db.Context.PaymentTransactions.SingleAsync(t => t.InvoiceId == invoice.Id);
            Assert.Equal(TransactionStatus.Pending, intent.Status);
            Assert.Equal(PaymentMethod.Cash, intent.Method);
            Assert.Equal(1200, intent.Amount);

            // The invoice only changes once an admin records the collected cash
            var storedInvoice = await _db.Context.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.NotEqual(InvoiceStatus.Paid, storedInvoice.Status);
            Assert.Equal(0, storedInvoice.AmountPaid);
        }

        [Fact]
        public async Task GenerateSchedule_CreatesAllCourseSessions_SkippingHolidays()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 4, includeSession: false);
            _db.Context.Holidays.Add(new Holiday { Name = "Holiday", Date = new DateOnly(2026, 8, 3) });
            await _db.Context.SaveChangesAsync();

            var sessions = await CreateSessionService().GenerateScheduleAsync(batch.Id, new GenerateScheduleRequest
            {
                StartDate = new DateOnly(2026, 8, 3), // a Monday that is a holiday
                DaysOfWeek = [DayOfWeek.Monday],
                StartTimeUtc = new TimeOnly(4, 30),
            });

            Assert.Equal(4, sessions.Count);
            Assert.DoesNotContain(sessions, s => DateOnly.FromDateTime(s.ScheduledStartAtUtc) == new DateOnly(2026, 8, 3));
        }

        [Fact]
        public async Task CompleteSession_AccruesPayoutEarning_AtConfiguredRate()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            await CreatePayoutService().SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 1100,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            await CreateSessionService().CompleteAsync(session.Id);

            var payout = Assert.Single(_db.Context.Payouts.ToList());
            var item = Assert.Single(_db.Context.PayoutItems.ToList());
            Assert.Equal(PayoutItemType.SessionEarning, item.Type);
            Assert.Equal(1100, item.Amount);
            Assert.Equal(1100, payout.TotalAmount);
            Assert.Equal(PayoutStatus.Pending, payout.Status);
        }

        [Fact]
        public async Task StudentNoShow_AddsWaitingAmount_AndCarriesSessionForward()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 2);
            await CreatePayoutService().SetRateAsync(new SavePayoutRateRequest
            {
                TeacherProfileId = session.TeacherProfileId,
                DurationMinutes = 45,
                RatePerSession = 1100,
                EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            });

            var carried = await CreateSessionService().MarkNoShowAsync(
                session.Id, new MarkNoShowRequest { Party = NoShowParty.Student });

            var original = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.StudentNoShow, original!.Status);
            Assert.Equal(session.ScheduledStartAtUtc.AddDays(7), carried.ScheduledStartAtUtc);
            var item = Assert.Single(_db.Context.PayoutItems.ToList());
            Assert.Equal(PayoutItemType.StudentNoShowWaiting, item.Type);
            Assert.Equal(1100, item.Amount);
        }

        [Fact]
        public async Task RecordEngagement_Allows_AssignedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var teacherProfile = await _db.Context.TeacherProfiles.FindAsync(session.TeacherProfileId);
            _db.CurrentUser.UserId = teacherProfile!.UserId;

            await CreateSessionService().RecordEngagementAsync(session.Id, EngagementRequest());

            Assert.Single(_db.Context.EngagementEvents.ToList());
        }

        [Fact]
        public async Task RecordEngagement_Rejects_UnrelatedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var otherTeacherUser = await _db.SeedUserAsync($"t2-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            _db.Context.TeacherProfiles.Add(new TeacherProfile { UserId = otherTeacherUser.Id });
            await _db.Context.SaveChangesAsync();
            _db.CurrentUser.UserId = otherTeacherUser.Id;

            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateSessionService().RecordEngagementAsync(session.Id, EngagementRequest()));
        }

        [Fact]
        public async Task RecordEngagement_Allows_ParentWithChildInBatch()
        {
            var (batch, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "One" };
            _db.Context.AddRange(parentProfile, child);
            await _db.Context.SaveChangesAsync();
            _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, ChildId = child.Id });
            await _db.Context.SaveChangesAsync();
            _db.CurrentUser.UserId = parentUser.Id;

            await CreateSessionService().RecordEngagementAsync(session.Id, EngagementRequest());

            Assert.Single(_db.Context.EngagementEvents.ToList());
        }

        [Fact]
        public async Task RecordEngagement_Rejects_ParentWithoutChildInBatch()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var parentUser = await _db.SeedUserAsync($"p2-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "Two" };
            _db.Context.AddRange(parentProfile, child);
            await _db.Context.SaveChangesAsync();
            _db.CurrentUser.UserId = parentUser.Id;

            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateSessionService().RecordEngagementAsync(session.Id, EngagementRequest()));
        }

        /// <summary>
        /// Makes the acting user a Teacher with no connection to <paramref name="session"/> —
        /// the "any teacher who knows a session id" attacker the session endpoints must reject.
        /// </summary>
        private async Task BecomeUnrelatedTeacherAsync()
        {
            var otherTeacherUser = await _db.SeedUserAsync($"t-other-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            _db.Context.TeacherProfiles.Add(new TeacherProfile { UserId = otherTeacherUser.Id });
            await _db.Context.SaveChangesAsync();
            _db.CurrentUser.UserId = otherTeacherUser.Id;
        }

        [Fact]
        public async Task CompleteSession_Rejects_UnrelatedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            await BecomeUnrelatedTeacherAsync();

            // Completing accrues a payout against the session's OWN teacher, so an outsider
            // must not be able to trigger it by naming a session id.
            await Assert.ThrowsAsync<ForbiddenException>(() => CreateSessionService().CompleteAsync(session.Id));

            var untouched = await _db.Context.ClassSessions.FindAsync(session.Id);
            Assert.Equal(SessionStatus.Scheduled, untouched!.Status);
            Assert.Empty(_db.Context.PayoutItems.ToList());
        }

        [Fact]
        public async Task MarkNoShow_Rejects_UnrelatedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            await BecomeUnrelatedTeacherAsync();

            // A teacher no-show is a deduction on the assigned teacher's pay — one teacher
            // filing it against another's class would be a direct financial attack.
            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateSessionService().MarkNoShowAsync(session.Id, new MarkNoShowRequest { Party = NoShowParty.Teacher }));

            Assert.Empty(_db.Context.PayoutItems.ToList());
            Assert.Equal(SessionStatus.Scheduled, (await _db.Context.ClassSessions.FindAsync(session.Id))!.Status);
        }

        [Fact]
        public async Task SessionAttendance_Rejects_UnrelatedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            await BecomeUnrelatedTeacherAsync();

            // Attendance rows name real children; reading or writing another class's is
            // neither the outsider's data nor their record to change.
            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateAcademicOpsService().ListAttendanceAsync(session.Id));
            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateAcademicOpsService().CaptureAttendanceAsync(session.Id, new CaptureAttendanceRequest
                {
                    Entries = [new AttendanceEntryDto { TeacherProfileId = session.TeacherProfileId, Status = AttendanceStatus.Present }],
                }));
            Assert.Empty(_db.Context.SessionAttendances.ToList());
        }

        [Fact]
        public async Task SessionRecordings_Reject_UnrelatedTeacher()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            await BecomeUnrelatedTeacherAsync();

            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateSessionService().ListRecordingsAsync(session.Id));
            await Assert.ThrowsAsync<ForbiddenException>(
                () => CreateSessionService().AddRecordingAsync(session.Id, new RegisterRecordingRequest
                {
                    StorageUrl = "https://recordings.test/evil.mp4",
                }));
            Assert.Empty(_db.Context.SessionRecordings.ToList());
        }

        [Fact]
        public async Task RequestRefund_Rejects_TransactionThatNeverSucceeded()
        {
            var parentUser = await _db.SeedUserAsync($"ref-pending-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });

            // A cash intent the parent declared but nobody has collected: Pending, so no
            // money has actually reached the platform to give back.
            await billing.InitiateParentPaymentAsync(parentUser.Id, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "cash" });
            var pending = await _db.Context.PaymentTransactions.FirstAsync(t => t.InvoiceId == invoice.Id);
            Assert.Equal(TransactionStatus.Pending, pending.Status);

            await Assert.ThrowsAsync<DomainValidationException>(
                () => billing.RequestRefundAsync(new RequestRefundRequest
                {
                    PaymentTransactionId = pending.Id, Amount = 1000, Reason = "Refund of money never received",
                }));
            Assert.Empty(_db.Context.Refunds.ToList());
        }

        [Fact]
        public async Task ApproveEnrollment_PersistsStatus_UnlocksParent_AndCreatesChild()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            var result = await service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true,
                ChildFirstName = "Kid",
                ChildLastName = "One",
                ChildDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-8),
            });

            Assert.Equal(EnrollmentFormStatus.Approved, result.Status);
            var refreshedParent = await _db.Context.ParentProfiles.FirstAsync(p => p.Id == parentProfile.Id);
            Assert.True(refreshedParent.EnrollmentFormCompleted);
            Assert.Single(_db.Context.Children.ToList());
        }

        [Fact]
        public async Task ApproveEnrollment_RejectsAFutureDateOfBirth()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            await Assert.ThrowsAsync<DomainValidationException>(() => service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true,
                ChildFirstName = "Kid",
                ChildLastName = "One",
                ChildDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1),
            }));

            Assert.Empty(_db.Context.Children.ToList());
            var form = await _db.Context.EnrollmentForms.FirstAsync(f => f.Id == formId);
            Assert.Equal(EnrollmentFormStatus.Submitted, form.Status); // rejected before anything was mutated
        }

        [Fact]
        public async Task ApproveEnrollment_RequiresADateOfBirth_ButRejectingNeverNeedsOne()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            // Not [Required] on the DTO — a reject request never touches this field and
            // must not be blocked by its absence.
            var rejected = await service.ReviewAsync(formId, new ReviewEnrollmentFormRequest { Approve = false });
            Assert.Equal(EnrollmentFormStatus.Rejected, rejected.Status);
        }

        [Fact]
        public async Task ApproveEnrollment_WithPackagePlan_StartsSubscription_AndIssuesFirstInvoice()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var plan = new PackagePlan { Name = "Phonics Monthly", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 2500 };
            _db.Context.AddRange(parentProfile, plan,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            var result = await service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true,
                ChildFirstName = "Kid",
                ChildLastName = "One",
                ChildDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-8),
                PackagePlanId = plan.Id,
            });

            Assert.Equal(EnrollmentFormStatus.Approved, result.Status);
            var child = Assert.Single(_db.Context.Children.ToList());
            var subscription = Assert.Single(_db.Context.Subscriptions.ToList());
            Assert.Equal(child.Id, subscription.ChildId);
            Assert.Equal(plan.Id, subscription.PackagePlanId);
            Assert.Equal(SubscriptionStatus.Active, subscription.Status);
            var invoice = Assert.Single(_db.Context.Invoices.ToList());
            Assert.Equal(plan.Price, invoice.Amount);
            Assert.Equal(child.Id, invoice.ChildId);
        }

        [Fact]
        public async Task ApproveEnrollment_WithPlanButNoPaymentAccount_FailsWithoutApproving()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var plan = new PackagePlan { Name = "Unroutable", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 900 };
            _db.Context.AddRange(parentProfile, plan);
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            await Assert.ThrowsAsync<DomainValidationException>(() => service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true,
                ChildFirstName = "Kid",
                PackagePlanId = plan.Id,
            }));

            // The bad billing pick must not leave a half-approved form behind.
            Assert.Equal(EnrollmentFormStatus.Submitted, (await service.GetAsync(formId)).Status);
            Assert.Empty(_db.Context.Children.ToList());
            Assert.Empty(_db.Context.Subscriptions.ToList());
        }

        [Fact]
        public async Task AssignStudent_PlacesChildInBatch_AndNotifiesParent()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "One", IsActive = true };
            _db.Context.AddRange(parentProfile, child);
            await _db.Context.SaveChangesAsync();

            var service = CreateBatchService();
            var result = await service.AssignStudentAsync(batch.Id, child.Id);

            Assert.Equal(child.Id, result.ChildId);
            Assert.Equal(EnrollmentStatus.Active, result.Status);
            var enrollment = Assert.Single(_db.Context.BatchEnrollments.ToList());
            Assert.Equal(batch.Id, enrollment.BatchId);
            Assert.Equal(1, (await service.GetAsync(batch.Id)).EnrolledCount);
            Assert.Contains(_emailSender.Sent, m => m.To == parentUser.Email && m.Subject.Contains("assigned to a batch"));
        }

        [Fact]
        public async Task AssignStudent_RejectsWhenBatchIsAtCapacity()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", Department = Department.Phonics };
            var course = new Course { CourseCategory = category, Name = "Course", Type = CourseType.Group, DurationMinutes = 45, Price = 100, TotalSessions = 1, Department = Department.Phonics };
            var batch = new Batch { Course = course, TeacherProfile = teacher, Name = "Full Batch", Capacity = 1 };
            var parentProfile = new ParentProfile { UserId = (await _db.SeedUserAsync($"p1-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id };
            var seatedChild = new Child { ParentProfile = parentProfile, FirstName = "Seated", LastName = "Kid", IsActive = true };
            _db.Context.AddRange(teacher, category, course, batch, parentProfile, seatedChild);
            await _db.Context.SaveChangesAsync();

            var service = CreateBatchService();
            await service.AssignStudentAsync(batch.Id, seatedChild.Id);

            var otherParentProfile = new ParentProfile { UserId = (await _db.SeedUserAsync($"p2-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id };
            var waitingChild = new Child { ParentProfile = otherParentProfile, FirstName = "Waiting", LastName = "Kid", IsActive = true };
            _db.Context.AddRange(otherParentProfile, waitingChild);
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() => service.AssignStudentAsync(batch.Id, waitingChild.Id));
        }

        [Fact]
        public async Task AssignStudent_RejectsDuplicate_ButAllowsReassignAfterRemoval()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "One", IsActive = true };
            _db.Context.AddRange(parentProfile, child);
            await _db.Context.SaveChangesAsync();

            var service = CreateBatchService();
            await service.AssignStudentAsync(batch.Id, child.Id);

            // Already-active: rejected, and the unique (BatchId, ChildId) index means a second
            // INSERT would be blocked at the DB level too — the service must catch it earlier.
            await Assert.ThrowsAsync<ConflictException>(() => service.AssignStudentAsync(batch.Id, child.Id));

            await service.RemoveStudentAsync(batch.Id, child.Id);
            Assert.Equal(0, (await service.GetAsync(batch.Id)).EnrolledCount);
            var withdrawn = Assert.Single(_db.Context.BatchEnrollments.ToList());
            Assert.Equal(EnrollmentStatus.Withdrawn, withdrawn.Status);

            // Re-assigning must reactivate the existing (unique-indexed) row, not insert a new one.
            await service.AssignStudentAsync(batch.Id, child.Id);
            Assert.Equal(1, (await service.GetAsync(batch.Id)).EnrolledCount);
            Assert.Single(_db.Context.BatchEnrollments.ToList());
        }

        [Fact]
        public async Task ListUnassignedStudents_ExcludesAlreadyEnrolled_AndInactiveChildren()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var enrolledParent = new ParentProfile { UserId = (await _db.SeedUserAsync($"p1-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id };
            var enrolledChild = new Child { ParentProfile = enrolledParent, FirstName = "Enrolled", LastName = "Kid", IsActive = true };
            var inactiveParent = new ParentProfile { UserId = (await _db.SeedUserAsync($"p2-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id };
            var inactiveChild = new Child { ParentProfile = inactiveParent, FirstName = "Inactive", LastName = "Kid", IsActive = false };
            var eligibleParent = new ParentProfile { UserId = (await _db.SeedUserAsync($"p3-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id };
            var eligibleChild = new Child { ParentProfile = eligibleParent, FirstName = "Eligible", LastName = "Kid", IsActive = true };
            _db.Context.AddRange(enrolledParent, enrolledChild, inactiveParent, inactiveChild, eligibleParent, eligibleChild);
            await _db.Context.SaveChangesAsync();

            var service = CreateBatchService();
            await service.AssignStudentAsync(batch.Id, enrolledChild.Id);

            var unassigned = await service.ListUnassignedStudentsAsync(batch.Id);

            Assert.DoesNotContain(unassigned, c => c.ChildId == enrolledChild.Id);
            Assert.DoesNotContain(unassigned, c => c.ChildId == inactiveChild.Id);
            Assert.Contains(unassigned, c => c.ChildId == eligibleChild.Id);
        }

        [Fact]
        public async Task UpdateEnrollmentForm_PersistsEditedAnswers_AndRejectsApprovedForms()
        {
            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = parentUser.Id });
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Old Name\",\"dob\":\"2016-01-01\",\"grade\":\"1\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;

            var edited = await service.UpdateFormDataAsync(formId, new SubmitEnrollmentFormRequest
            {
                FormDataJson = "{\"childName\":\"New Name\",\"dob\":\"2016-01-01\",\"grade\":\"2\",\"courseInterest\":\"Math\"}",
            });
            Assert.Contains("New Name", edited.FormDataJson);
            var reloaded = await service.GetAsync(formId);
            Assert.Contains("New Name", reloaded.FormDataJson);

            // Once approved, the form is immutable.
            await service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true,
                ChildFirstName = "New",
                ChildLastName = "Name",
                ChildDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-8),
            });
            await Assert.ThrowsAsync<ConflictException>(
                () => service.UpdateFormDataAsync(formId, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Later\",\"dob\":\"2016-01-01\",\"grade\":\"2\",\"courseInterest\":\"Math\"}" }));
        }

        [Fact]
        public async Task Store_PublicPlans_OnlyListsActivePlans()
        {
            var active = new PackagePlan { Name = "Active Plan", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 1500, IsActive = true };
            var inactive = new PackagePlan { Name = "Retired Plan", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 1200, IsActive = false };
            _db.Context.AddRange(active, inactive);
            await _db.Context.SaveChangesAsync();

            var plans = await CreateStoreService().ListPublicPlansAsync();

            Assert.Contains(plans, p => p.Id == active.Id);
            Assert.DoesNotContain(plans, p => p.Id == inactive.Id);
        }

        [Fact]
        public async Task Store_CreateInquiry_RejectsInactivePlan_AndAdminCanTransitionStatus()
        {
            var plan = new PackagePlan { Name = "Phonics Trial", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 1800, IsActive = true };
            var retired = new PackagePlan { Name = "Old Plan", BillingType = BillingType.Subscription, BillingCycle = BillingCycle.Monthly, Price = 900, IsActive = false };
            _db.Context.AddRange(plan, retired);
            await _db.Context.SaveChangesAsync();

            var service = CreateStoreService();

            await Assert.ThrowsAsync<NotFoundException>(() => service.CreateInquiryAsync(new CreateStoreInquiryRequest
            {
                PackagePlanId = retired.Id,
                ParentName = "Rohit Kapoor",
                ParentEmail = "rohit@example.com",
                ParentPhone = "9876543210",
                ChildName = "Aarav",
            }));

            var inquiry = await service.CreateInquiryAsync(new CreateStoreInquiryRequest
            {
                PackagePlanId = plan.Id,
                ParentName = "Rohit Kapoor",
                ParentEmail = "Rohit@Example.com",
                ParentPhone = "9876543210",
                ChildName = "Aarav",
                ChildAge = 6,
            });
            Assert.Equal(StoreInquiryStatus.New, inquiry.Status);
            Assert.Equal("rohit@example.com", inquiry.ParentEmail); // normalized lowercase

            var listed = await service.ListInquiriesAsync(StoreInquiryStatus.New);
            Assert.Single(listed);

            var updated = await service.UpdateInquiryStatusAsync(inquiry.Id, new UpdateStoreInquiryStatusRequest { Status = StoreInquiryStatus.Contacted });
            Assert.Equal(StoreInquiryStatus.Contacted, updated.Status);

            Assert.Empty(await service.ListInquiriesAsync(StoreInquiryStatus.New));
        }

        [Fact]
        public async Task Store_BookDemo_AutoAssignsTeacher_AndCreatesSession()
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            _db.Context.TeacherProfiles.Add(new TeacherProfile { UserId = teacherUser.Id });
            await _db.Context.SaveChangesAsync();

            var confirmation = await CreateStoreService().BookDemoAsync(new CreateStoreDemoBookingRequest
            {
                ParentName = "Visitor Parent",
                ParentEmail = "visitor@example.com",
                ParentPhone = "9876500000",
                ChildName = "Kid",
                ChildAge = 7,
                PreferredStartAtUtc = DateTime.UtcNow.AddDays(1),
            });

            Assert.Equal(30, (confirmation.ScheduledEndAtUtc - confirmation.ScheduledStartAtUtc).TotalMinutes);
            var booking = await _db.Context.DemoBookings.FirstAsync(b => b.Id == confirmation.Id);
            Assert.NotNull(booking.ClassSessionId);
            var session = await _db.Context.ClassSessions.FirstAsync(s => s.Id == booking.ClassSessionId!.Value);
            Assert.Equal(SessionType.Demo, session.Type);
            Assert.NotEqual(Guid.Empty, session.TeacherProfileId); // auto-assigned, never left blank
        }

        [Fact]
        public async Task Store_BookDemo_ConcurrentRequestsForSameSlot_MustNotDoubleBookTheOnlyTeacher()
        {
            // IMPORTANT SCOPE NOTE — what this does and does not prove. It proves the
            // serialized case: once one booking is committed, a second request for the same
            // slot is refused rather than double-booking the teacher, and the whole flow still
            // works now that it runs inside a SERIALIZABLE transaction. It does NOT prove the
            // genuinely concurrent case, because it cannot: both DbContexts here share one
            // SqliteConnection and a single ADO.NET connection runs one command at a time, so
            // the two requests below execute back to back, never overlapping. The concurrent
            // guarantee comes from where this now runs — CreateAsync wraps the busy-check and
            // the insert in one IUnitOfWork.ExecuteInSerializableTransactionAsync, so on
            // Postgres SSI aborts one of two truly overlapping bookings with SQLSTATE 40001 and
            // the unit of work retries it against the committed state (see that method's docs,
            // and UnitOfWork_SerializableTransaction_* for the retry machinery itself). With no
            // Postgres in this environment that half rests on SSI's documented semantics, not
            // on an observed run.
            var teacherUser = await _db.SeedUserAsync($"race-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            _db.Context.TeacherProfiles.Add(new TeacherProfile { UserId = teacherUser.Id });
            await _db.Context.SaveChangesAsync();

            var start = DateTime.UtcNow.AddDays(1);

            // Two independent service graphs on two independent DbContexts sharing the same
            // underlying SQLite connection — the same shape as two concurrent HTTP requests
            // each getting their own scoped DbContext in ASP.NET Core.
            var (context2, uow2) = _db.CreateConcurrentSession();
            var auditLog2 = new AuditLogService(uow2, _db.CurrentUser);
            var emailTemplates2 = new EmailTemplateService(uow2, auditLog2, new MemoryCache(new MemoryCacheOptions()));
            var service1 = CreateStoreService();
            var service2 = new StoreService(
                uow2, auditLog2,
                new DemoBookingService(uow2, auditLog2, _emailSender, emailTemplates2, new FakeCrmNotifier(), new FakeJitsiTokenService(), NullLogger<DemoBookingService>.Instance));

            var request1 = new CreateStoreDemoBookingRequest
            {
                ParentName = "Parent One", ParentEmail = "race1@test.com", ParentPhone = "9000000001",
                ChildName = "Kid One", PreferredStartAtUtc = start,
            };
            var request2 = new CreateStoreDemoBookingRequest
            {
                ParentName = "Parent Two", ParentEmail = "race2@test.com", ParentPhone = "9000000002",
                ChildName = "Kid Two", PreferredStartAtUtc = start, // identical, fully-overlapping slot
            };

            var task1 = service1.BookDemoAsync(request1);
            var task2 = service2.BookDemoAsync(request2);

            Exception? failure = null;
            try
            {
                await Task.WhenAll(task1, task2);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Ground truth: how many demo sessions actually landed on the single teacher
            // for this exact slot, read fresh from a third, independent context.
            var (verifyContext, _) = _db.CreateConcurrentSession();
            var overlapping = await verifyContext.ClassSessions
                .Where(s => s.Type == SessionType.Demo && s.ScheduledStartAtUtc == start)
                .CountAsync();

            if (overlapping > 1)
            {
                Assert.Fail(
                    $"Double-booking confirmed: {overlapping} overlapping demo sessions were created for the " +
                    "same teacher and time slot, with no rejection. " +
                    $"(Task fault, if any: {failure?.Message ?? "none — both requests succeeded"})");
            }

            // Exactly one request must have succeeded and the other must have been refused with
            // the expected "no teacher available" message — a silent no-op, a mismatched error,
            // or a transaction-plumbing failure (e.g. an unsupported isolation level surfacing
            // as an InvalidOperationException) would all be bugs this catches.
            Assert.Equal(1, overlapping);
            var refusal = failure is AggregateException aggregate ? aggregate.InnerExceptions[0] : failure;
            Assert.IsType<DomainValidationException>(refusal);
            Assert.Contains("No teacher is available", refusal!.Message);

            // The losing request must leave nothing behind: its rolled-back transaction means no
            // orphan DemoBooking for the parent whose session was never created.
            Assert.Equal(1, await verifyContext.DemoBookings.CountAsync(b => b.ChildName == "Kid One" || b.ChildName == "Kid Two"));

            context2.Dispose();
            verifyContext.Dispose();
        }

        [Fact]
        public async Task UnitOfWork_SerializableTransaction_RetriesOnSerializationFailure_AndDiscardsTheFailedAttemptsWrites()
        {
            // The retry machinery the demo-booking fix depends on. SQLite never raises SQLSTATE
            // 40001, so the abort is injected in the shape SSI actually delivers it: the INSERT
            // itself is the statement that loses, so attempt 1 dies with its entity staged but
            // not yet persisted. Those entities stay in the change tracker in the Added state
            // across the rollback, so unless the unit of work forgets them, the retry's
            // SaveChanges inserts BOTH the abandoned entity and the fresh one — which, Name
            // being uniquely indexed, blows up rather than quietly duplicating.
            var attempts = 0;
            var result = await _db.UnitOfWork.ExecuteInSerializableTransactionAsync(async ct =>
            {
                attempts++;
                await _db.UnitOfWork.Repository<CourseCategory>().AddAsync(
                    new CourseCategory { Name = "Retry Category", Department = Department.Phonics }, ct);

                if (attempts == 1)
                {
                    throw new FakeSerializationFailure();
                }

                await _db.UnitOfWork.SaveChangesAsync(ct);
                return attempts;
            });

            Assert.Equal(2, result); // retried exactly once, and the retry is what succeeded
            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(1, await verifyContext.CourseCategories.CountAsync(c => c.Name == "Retry Category"));
            verifyContext.Dispose();
        }

        [Fact]
        public async Task UnitOfWork_SerializableTransaction_DoesNotRetryOrdinaryFailures_AndRollsThemBack()
        {
            // Only a serialization failure is safe to redo blindly. A business failure must
            // surface once, unchanged, with everything the attempt wrote rolled back — otherwise
            // "no teacher available" would be retried three more times to the same conclusion.
            var attempts = 0;
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                _db.UnitOfWork.ExecuteInSerializableTransactionAsync<int>(async ct =>
                {
                    attempts++;
                    await _db.UnitOfWork.Repository<CourseCategory>().AddAsync(
                        new CourseCategory { Name = "Doomed Category", Department = Department.Phonics }, ct);
                    await _db.UnitOfWork.SaveChangesAsync(ct);
                    throw new DomainValidationException("business rule said no");
                }));

            Assert.Equal(1, attempts);
            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(0, await verifyContext.CourseCategories.CountAsync(c => c.Name == "Doomed Category"));
            verifyContext.Dispose();
        }

        [Fact]
        public async Task Store_BookDemo_RejectsTooSoonAndTooFarOut()
        {
            var service = CreateStoreService();
            var request = new CreateStoreDemoBookingRequest
            {
                ParentName = "Visitor Parent",
                ParentEmail = "visitor2@example.com",
                ParentPhone = "9876500001",
                ChildName = "Kid",
                PreferredStartAtUtc = DateTime.UtcNow.AddMinutes(30), // under the 2-hour lead time
            };

            await Assert.ThrowsAsync<DomainValidationException>(() => service.BookDemoAsync(request));

            request.PreferredStartAtUtc = DateTime.UtcNow.AddDays(90); // past the 30-day window
            await Assert.ThrowsAsync<DomainValidationException>(() => service.BookDemoAsync(request));
        }

        [Fact]
        public async Task ProgressReport_SaveThenSend_LocksContentAndEmailsParent()
        {
            var parentUser = await _db.SeedUserAsync($"pr-parent-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Aarav", LastName = "Kid", IsActive = true };
            _db.Context.AddRange(parentProfile, child);
            await _db.Context.SaveChangesAsync();

            var service = CreateProgressReportService();
            var created = await service.EnsureMonthlyDraftsAsync(2026, 8);
            Assert.Equal(1, created);

            var draft = (await service.ListAsync(2026, 8, child.Id)).Single();
            Assert.Equal(ProgressReportStatus.Draft, draft.Status);
            Assert.Equal(string.Empty, draft.Content);

            // Sending an empty draft is rejected — there's nothing for the parent to read yet.
            await Assert.ThrowsAsync<DomainValidationException>(() => service.SendAsync(draft.Id));

            var saved = await service.SaveContentAsync(draft.Id, new SaveProgressReportContentRequest
            {
                Content = "Aarav is making great progress with blending sounds this month.",
            });
            Assert.Equal(ProgressReportStatus.Draft, saved.Status);

            var sent = await service.SendAsync(draft.Id);
            Assert.Equal(ProgressReportStatus.Sent, sent.Status);
            Assert.NotNull(sent.SentAtUtc);

            var email = Assert.Single(_emailSender.Sent, e => e.To == parentUser.Email);
            Assert.Contains("blending sounds", email.Body);

            // A sent report is locked: no further content edits, no re-sending.
            await Assert.ThrowsAsync<DomainValidationException>(
                () => service.SaveContentAsync(draft.Id, new SaveProgressReportContentRequest { Content = "Edited after send" }));
            await Assert.ThrowsAsync<DomainValidationException>(() => service.SendAsync(draft.Id));
        }

        [Fact]
        public async Task ProgressReport_EnsureMonthlyDrafts_SkipsInactiveChildrenAndIsIdempotent()
        {
            var parentUser = await _db.SeedUserAsync($"pr-parent2-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var activeChild = new Child { ParentProfile = parentProfile, FirstName = "Active", LastName = "Kid", IsActive = true };
            var inactiveChild = new Child { ParentProfile = parentProfile, FirstName = "Inactive", LastName = "Kid", IsActive = false };
            _db.Context.AddRange(parentProfile, activeChild, inactiveChild);
            await _db.Context.SaveChangesAsync();

            var service = CreateProgressReportService();
            var firstRun = await service.EnsureMonthlyDraftsAsync(2026, 9);
            Assert.Equal(1, firstRun); // only the active child gets a draft

            var secondRun = await service.EnsureMonthlyDraftsAsync(2026, 9);
            Assert.Equal(0, secondRun); // already exists — no duplicate row for the same period

            var reports = await service.ListAsync(2026, 9, null);
            Assert.Single(reports);
            Assert.Equal(activeChild.Id, reports[0].ChildId);
        }

        [Fact]
        public void HtmlText_PlainTextFromHtml_StripsMarkupAndBrandChrome()
        {
            const string rendered =
                "<div style=\"font-family:Arial,Helvetica,sans-serif;\"><div style=\"background:#4F46E5;\">" +
                "<span style=\"color:#ffffff;\">The Reader Nest</span></div><div><p>Your child's class starts at " +
                "<strong>Wed, 05 Aug 2026 3:30 PM (Asia/Kolkata)</strong>.</p><p><a href=\"https://meet.example.com/x\">" +
                "Join Now</a></p></div><p>The Reader Nest &middot; Read &middot; Write &middot; Speak</p></div>";

            var plain = iucs.readernest.application.Common.HtmlText.PlainTextFromHtml(rendered);

            Assert.DoesNotContain('<', plain);
            Assert.DoesNotContain('>', plain);
            Assert.Contains("Your child's class starts at Wed, 05 Aug 2026 3:30 PM (Asia/Kolkata) . Join Now", plain);
            Assert.DoesNotContain("The Reader Nest", plain); // header/footer chrome stripped, not just tags
        }

        /// <summary>
        /// Regression test: the DatabaseInitializer backfill filters Notification.Body with
        /// `.Contains('<')` (char overload) which Npgsql can't translate to SQL and crashes the
        /// whole app at startup — caught only by actually running the query through a real EF
        /// provider, not by unit-testing HtmlText in isolation. Mirrors the exact predicate shape
        /// used there so a future regression to the char overload fails here too.
        /// </summary>
        [Fact]
        public async Task NotificationQuery_ContainsStringPredicate_TranslatesAndFiltersCorrectly()
        {
            var user = await _db.SeedUserAsync($"notif-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var staleHtml = new Notification
            {
                RecipientUserId = user.Id,
                Type = NotificationType.SessionReminder,
                Channel = NotificationChannel.Email,
                Subject = "Class starts in 1 hour",
                Body = "<div style=\"padding:28px;\"><p>Your child's class starts soon.</p></div>",
                Status = NotificationStatus.Sent,
            };
            var alreadyPlain = new Notification
            {
                RecipientUserId = user.Id,
                Type = NotificationType.SessionReminder,
                Channel = NotificationChannel.Email,
                Subject = "Class starts in 1 hour",
                Body = "Your child's class starts at Sat, 08 Aug 2026 3:30 PM. Join Now",
                Status = NotificationStatus.Sent,
            };
            _db.Context.AddRange(staleHtml, alreadyPlain);
            await _db.Context.SaveChangesAsync();

            var matched = await _db.Context.Notifications
                .Where(n => n.Body.Contains("<") && n.Body.Contains(">"))
                .ToListAsync();

            Assert.Contains(matched, n => n.Id == staleHtml.Id);
            Assert.DoesNotContain(matched, n => n.Id == alreadyPlain.Id);
        }

        [Fact]
        public async Task ParentDashboard_AggregatesPerChild_WithoutCrossContamination()
        {
            var parentUser = await _db.SeedUserAsync($"dash-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.ParentProfiles.Add(parentProfile);
            await _db.Context.SaveChangesAsync();

            var (batchA, _, _) = await SeedBatchWithSessionAsync(10, includeSession: false);
            var (batchB, _, _) = await SeedBatchWithSessionAsync(10, includeSession: false);

            // Alice sits in batch A, Bob in batch B, so a per-child aggregate that leaked
            // across siblings (or across batches) shows up as the wrong count below.
            var alice = new Child { ParentProfileId = parentProfile.Id, FirstName = "Alice", LastName = "A", IsActive = true };
            var bob = new Child { ParentProfileId = parentProfile.Id, FirstName = "Bob", LastName = "B", IsActive = true };
            _db.Context.AddRange(alice, bob);
            await _db.Context.SaveChangesAsync();

            _db.Context.AddRange(
                new BatchEnrollment { BatchId = batchA.Id, ChildId = alice.Id, Status = EnrollmentStatus.Active },
                new BatchEnrollment { BatchId = batchB.Id, ChildId = bob.Id, Status = EnrollmentStatus.Active });

            ClassSession SessionIn(Batch batch, SessionStatus status) => new()
            {
                BatchId = batch.Id,
                TeacherProfileId = batch.TeacherProfileId,
                Status = status,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(1),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(1).AddMinutes(45),
            };

            // Batch A: 2 completed + 1 scheduled. Batch B: 1 completed only.
            var aliceCompleted = SessionIn(batchA, SessionStatus.Completed);
            _db.Context.AddRange(
                aliceCompleted,
                SessionIn(batchA, SessionStatus.Completed),
                SessionIn(batchA, SessionStatus.Scheduled),
                SessionIn(batchB, SessionStatus.Completed));
            await _db.Context.SaveChangesAsync();

            // Alice: 1 of 2 attended → 50%. Bob has no attendance rows → defaults to 100%.
            _db.Context.AddRange(
                new SessionAttendance
                {
                    ClassSessionId = aliceCompleted.Id,
                    ChildId = alice.Id,
                    ParticipantType = ParticipantType.Student,
                    Status = AttendanceStatus.Present,
                },
                new SessionAttendance
                {
                    ClassSessionId = aliceCompleted.Id,
                    ChildId = bob.Id,
                    ParticipantType = ParticipantType.Student,
                    Status = AttendanceStatus.Present,
                });
            await _db.Context.SaveChangesAsync();

            // Bob's single row is Absent, so his percentage must differ from Alice's.
            var bobRow = await _db.Context.SessionAttendances.FirstAsync(a => a.ChildId == bob.Id);
            bobRow.Status = AttendanceStatus.Absent;
            await _db.Context.SaveChangesAsync();
            _db.Context.ChangeTracker.Clear();

            var dashboard = await new ParentPortalService(_db.UnitOfWork).GetDashboardAsync(parentUser.Id);

            var aliceDto = dashboard.Children.Single(c => c.ChildId == alice.Id);
            Assert.Equal(2, aliceDto.ClassesCompleted);
            Assert.Equal(1, aliceDto.ClassesRemaining);
            Assert.Equal(100, aliceDto.AttendancePercent);

            var bobDto = dashboard.Children.Single(c => c.ChildId == bob.Id);
            Assert.Equal(1, bobDto.ClassesCompleted);
            Assert.Equal(0, bobDto.ClassesRemaining);
            Assert.Equal(0, bobDto.AttendancePercent);
        }

        [Fact]
        public async Task MarkAllRead_StampsEveryUnreadRow_AndLeavesOtherRecipientsAlone()
        {
            var user = await _db.SeedUserAsync($"notif-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var otherUser = await _db.SeedUserAsync($"notif-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);

            Notification Unread(Guid recipientId) => new()
            {
                RecipientUserId = recipientId,
                Type = NotificationType.SessionReminder,
                Channel = NotificationChannel.InApp,
                Subject = "Class starts in 1 hour",
                Body = "Your child's class starts soon.",
                Status = NotificationStatus.Sent,
            };

            var alreadyRead = Unread(user.Id);
            alreadyRead.ReadAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var mine = new[] { Unread(user.Id), Unread(user.Id) };
            var theirs = Unread(otherUser.Id);
            _db.Context.AddRange([.. mine, alreadyRead, theirs]);
            await _db.Context.SaveChangesAsync();

            // MarkAllReadAsync is a single ExecuteUpdate, which bypasses the change tracker —
            // so this also pins that the audit interceptor's UpdatedAtUtc stamp is applied by
            // hand, and that the conditional WHERE really is scoped to one recipient's unread.
            var marked = await _notifications.MarkAllReadAsync(user.Id);
            Assert.Equal(mine.Length, marked);

            _db.Context.ChangeTracker.Clear();
            var rows = await _db.Context.Notifications.ToListAsync();

            foreach (var expected in mine)
            {
                var row = rows.Single(n => n.Id == expected.Id);
                Assert.NotNull(row.ReadAtUtc);
                Assert.NotNull(row.UpdatedAtUtc);
            }

            // An already-read row keeps its original timestamp, and another user's is untouched.
            Assert.Equal(alreadyRead.ReadAtUtc, rows.Single(n => n.Id == alreadyRead.Id).ReadAtUtc);
            Assert.Null(rows.Single(n => n.Id == theirs.Id).ReadAtUtc);

            // Second call has nothing left to do.
            Assert.Equal(0, await _notifications.MarkAllReadAsync(user.Id));
        }

        // ---- QA pass: object-level authorization on id-keyed teacher endpoints ----

        /// <summary>A demo booking with a real class session assigned to its own teacher.</summary>
        private async Task<(DemoBooking Booking, TeacherProfile OwningTeacher, User OtherTeacherUser)> SeedDemoBookingAsync()
        {
            var owningTeacherUser = await _db.SeedUserAsync($"demo-own-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var owningTeacher = new TeacherProfile { UserId = owningTeacherUser.Id };
            var otherTeacherUser = await _db.SeedUserAsync($"demo-other-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var otherTeacher = new TeacherProfile { UserId = otherTeacherUser.Id };
            _db.Context.AddRange(owningTeacher, otherTeacher);
            await _db.Context.SaveChangesAsync();

            var booking = await CreateDemoBookingService().CreateAsync(new CreateDemoBookingRequest
            {
                ParentName = "Lead Parent",
                ParentEmail = $"lead-{Guid.NewGuid():N}@test.com",
                ChildName = "Kid",
                TeacherProfileId = owningTeacher.Id,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(1),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(1).AddMinutes(30),
            });

            var entity = await _db.Context.DemoBookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id);
            return (entity, owningTeacher, otherTeacherUser);
        }

        [Fact]
        public async Task SubmitDemoFeedback_Rejects_UnrelatedTeacher()
        {
            // Same shape as the session IDOR fixed in 8895924: the endpoint is gated on
            // "is a Teacher" but the demo booking id comes straight from the caller. The
            // feedback is the permanent, admission-facing evaluation of a named child
            // (it carries RecommendedCourseId/SuggestedBatchType and drives enrollment),
            // it is filed under the CALLER's teacher profile, and it is one-shot — so a
            // teacher who never ran the demo can both falsify the record and permanently
            // lock the real teacher out of the mandatory post-demo step.
            var (booking, _, otherTeacherUser) = await SeedDemoBookingAsync();

            await Assert.ThrowsAsync<ForbiddenException>(() =>
                CreateDemoBookingService().SubmitFeedbackAsync(booking.Id, otherTeacherUser.Id, new SubmitDemoFeedbackRequest
                {
                    AcademicLevel = "Level 2",
                    Strengths = "Injected by a teacher who never taught this child",
                    ImprovementAreas = "n/a",
                }));

            // Ground truth: nothing was written, so the assigned teacher can still file theirs.
            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Empty(await verifyContext.DemoFeedbacks.Where(f => f.DemoBookingId == booking.Id).ToListAsync());
            verifyContext.Dispose();
        }

        [Fact]
        public async Task SubmitDemoFeedback_Allows_AssignedTeacher_AndIsOneShot()
        {
            var (booking, owningTeacher, _) = await SeedDemoBookingAsync();
            var owningTeacherUserId = (await _db.Context.TeacherProfiles.AsNoTracking()
                .FirstAsync(t => t.Id == owningTeacher.Id)).UserId;

            var request = new SubmitDemoFeedbackRequest
            {
                AcademicLevel = "Level 2",
                Strengths = "Confident reader",
                ImprovementAreas = "Blends",
            };

            var feedback = await CreateDemoBookingService().SubmitFeedbackAsync(booking.Id, owningTeacherUserId, request);
            Assert.Equal(booking.Id, feedback.DemoBookingId);

            // Feedback closes the demo stage of the conversion pipeline.
            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(
                ConversionStatus.DemoCompleted,
                (await verifyContext.DemoBookings.FirstAsync(b => b.Id == booking.Id)).ConversionStatus);
            verifyContext.Dispose();

            // Still one-shot for the legitimate teacher.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                CreateDemoBookingService().SubmitFeedbackAsync(booking.Id, owningTeacherUserId, request));
        }

        // ---- QA pass: state-machine guards (invalid transitions must be refused, not no-op'd) ----

        [Fact]
        public async Task CashIntent_CannotBeConfirmedOrRejectedTwice()
        {
            var (billing, invoice) = await SeedInvoiceAsync(amount: 1000);
            var parentUserId = await _db.Context.ParentProfiles.AsNoTracking()
                .Where(p => p.Id == invoice.ParentProfileId).Select(p => p.UserId).FirstAsync();

            await billing.InitiateParentPaymentAsync(parentUserId, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "cash" });
            var intent = await _db.Context.PaymentTransactions.AsNoTracking()
                .FirstAsync(t => t.InvoiceId == invoice.Id && t.Method == PaymentMethod.Cash);

            await billing.ConfirmCashIntentAsync(intent.Id, new ConfirmCashIntentRequest());

            // Re-confirming must not credit the invoice a second time.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                billing.ConfirmCashIntentAsync(intent.Id, new ConfirmCashIntentRequest()));
            // Nor may a settled intent be walked backwards into Failed.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                billing.RejectCashIntentAsync(intent.Id, new RejectCashIntentRequest { Reason = "changed my mind" }));

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var settled = await verifyContext.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(1000m, settled.AmountPaid); // credited exactly once
            Assert.Equal(InvoiceStatus.Paid, settled.Status);
            Assert.Equal(
                TransactionStatus.Success,
                (await verifyContext.PaymentTransactions.FirstAsync(t => t.Id == intent.Id)).Status);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task Refund_RejectedThenApproved_IsRefused_AndNeverReachesTheGateway()
        {
            var gateway = new FakePaymentGateway();
            var refund = await SeedRequestedRefundAsync(gateway);
            var billing = CreateBillingService(gateway);

            await billing.ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = false });

            // AppException, not a specific subclass: which of the two guards refuses this depends
            // on whether the caller's DbContext still holds the pre-rejection tracked entity.
            // In production each request is a fresh scope, so the friendly in-memory check wins
            // (400 "already Rejected"); reusing one scope as this test does leaves that read
            // stale — ExecuteUpdateAsync bypasses the change tracker — and the conditional UPDATE
            // catches it instead (409). Both are correct refusals; the invariant under test is
            // that a rejected refund is never resurrected and never reaches the gateway.
            await Assert.ThrowsAsync<ConflictException>(() =>
                billing.ReviewRefundAsync(refund.Id, new ReviewRefundRequest { Approve = true }));

            Assert.Equal(0, gateway.RefundCallCount); // no money left the platform
            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(RefundStatus.Rejected, (await verifyContext.Refunds.FirstAsync(r => r.Id == refund.Id)).Status);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task Session_TerminalStatuses_RefuseCompleteAndNoShowAndAttendance()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 3);
            var sessions = CreateSessionService();

            await sessions.CompleteAsync(session.Id, new CompleteSessionRequest());

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                sessions.CompleteAsync(session.Id, new CompleteSessionRequest()));
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                sessions.MarkNoShowAsync(session.Id, new MarkNoShowRequest { Party = NoShowParty.Teacher }));

            // Exactly one payout earning accrued, not two — the guard is what protects the
            // teacher's statement from a double-click on "complete".
            var (verifyContext, _) = _db.CreateConcurrentSession();
            var items = await verifyContext.PayoutItems.Where(i => i.ClassSessionId == session.Id).ToListAsync();
            Assert.Single(items);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task Payout_FinalizeAndMarkPaid_RefuseOutOfOrderAndRepeatTransitions()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var payouts = CreatePayoutService();
            await payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 45, RatePerSession = 500, EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            });
            await CreateSessionService().CompleteAsync(session.Id, new CompleteSessionRequest());
            var payout = await _db.Context.Payouts.AsNoTracking().FirstAsync();

            // Paying before finalizing skips the lock on the month's total.
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.MarkPaidAsync(payout.Id));

            await payouts.FinalizeAsync(payout.Id);
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.FinalizeAsync(payout.Id));

            await payouts.MarkPaidAsync(payout.Id);
            // A second mark-paid would email a duplicate salary slip against the same money.
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.MarkPaidAsync(payout.Id));

            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(PayoutStatus.Paid, (await verifyContext.Payouts.FirstAsync(p => p.Id == payout.Id)).Status);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task FeeSuspension_CannotBeLiftedTwice()
        {
            var parentUser = await _db.SeedUserAsync($"susp-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var account = new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" };
            _db.Context.AddRange(parentProfile, account);
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 500,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            });
            var suspension = new FeeSuspension
            {
                ParentProfileId = parentProfile.Id, InvoiceId = invoice.Id,
                Reason = "Overdue", SuspendedAtUtc = DateTime.UtcNow,
            };
            _db.Context.FeeSuspensions.Add(suspension);
            await _db.Context.SaveChangesAsync();

            var lifted = await billing.LiftSuspensionAsync(suspension.Id);
            Assert.Equal(SuspensionStatus.Lifted, lifted.Status);

            await Assert.ThrowsAsync<DomainValidationException>(() => billing.LiftSuspensionAsync(suspension.Id));
        }

        [Fact]
        public async Task ConfirmCashIntent_RecordsTheCollectedMoneyAsASuccessfulTransaction()
        {
            // Regression: confirming a cash intent that FULLY settles its invoice used to leave
            // the very transaction being confirmed marked Failed ("settled by another payment"),
            // because ApplyPaymentToInvoiceAsync's stale-intent sweep re-read it from the DB —
            // where the flip to Success was not committed yet — and swept it up as if it were a
            // competing intent. The invoice looked right, but the money's own row did not exist
            // as a successful payment: it disappeared from the invoice's receipt list and could
            // never be refunded. Only full settlement triggered it (a partial payment never
            // enters that branch), which is the common case for a cash intent.
            var (billing, invoice) = await SeedInvoiceAsync(amount: 1000);
            var parentUserId = await _db.Context.ParentProfiles.AsNoTracking()
                .Where(p => p.Id == invoice.ParentProfileId).Select(p => p.UserId).FirstAsync();

            await billing.InitiateParentPaymentAsync(parentUserId, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "cash" });
            var intent = await _db.Context.PaymentTransactions.AsNoTracking()
                .FirstAsync(t => t.InvoiceId == invoice.Id && t.Method == PaymentMethod.Cash);

            var confirmed = await billing.ConfirmCashIntentAsync(intent.Id, new ConfirmCashIntentRequest());
            Assert.Equal(1000m, confirmed.Amount);

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var row = await verifyContext.PaymentTransactions.FirstAsync(t => t.Id == intent.Id);
            Assert.Equal(TransactionStatus.Success, row.Status);
            Assert.Null(row.FailureReason);
            Assert.NotNull(row.PaidAtUtc);
            Assert.NotNull(row.ReceiptNumber); // the receipt handed to the parent at the centre
            verifyContext.Dispose();

            // The two things that consume Success transactions must both see the cash payment.
            var listed = Assert.Single(await billing.ListInvoiceTransactionsAsync(invoice.Id));
            Assert.Equal(intent.Id, listed.Id);

            var refund = await billing.RequestRefundAsync(new RequestRefundRequest
            {
                PaymentTransactionId = intent.Id, Amount = 250, Reason = "Goodwill on collected cash",
            });
            Assert.Equal(RefundStatus.Requested, refund.Status);
        }

        [Fact]
        public async Task RecordPayment_SettlingAPendingCashIntentInFull_KeepsItSuccessful()
        {
            // Same defect, sibling call site: RecordPaymentAsync reuses the parent's pending cash
            // intent instead of inserting a duplicate row, so it hit the identical sweep.
            var (billing, invoice) = await SeedInvoiceAsync(amount: 800);
            var parentUserId = await _db.Context.ParentProfiles.AsNoTracking()
                .Where(p => p.Id == invoice.ParentProfileId).Select(p => p.UserId).FirstAsync();

            await billing.InitiateParentPaymentAsync(parentUserId, invoice.Id, new InitiateParentPaymentRequest { MethodKey = "cash" });

            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 800, Method = PaymentMethod.Cash });

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var rows = await verifyContext.PaymentTransactions.Where(t => t.InvoiceId == invoice.Id).ToListAsync();
            var row = Assert.Single(rows); // reused, not duplicated
            Assert.Equal(TransactionStatus.Success, row.Status);
            Assert.Equal(InvoiceStatus.Paid, (await verifyContext.Invoices.FirstAsync(i => i.Id == invoice.Id)).Status);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task ConfirmCashIntent_StillClosesAGenuinelyCompetingIntent()
        {
            // The guard above must not blunt what the sweep is actually for: a DIFFERENT pending
            // cash intent on the same invoice still has to be closed when the money arrives by
            // another route, or it lingers in the staff confirmation queue and can be collected
            // a second time.
            var (billing, invoice) = await SeedInvoiceAsync(amount: 600);

            // Two pending cash intents on one invoice (an older declaration plus a fresh one).
            // Seeded directly: InitiateParentPaymentAsync deliberately supersedes prior intents.
            var older = new PaymentTransaction
            {
                InvoiceId = invoice.Id, PaymentAccountId = invoice.PaymentAccountId, Amount = 600,
                Currency = invoice.Currency, Status = TransactionStatus.Pending,
                GatewayTransactionId = $"CASH-{Guid.NewGuid():N}", Method = PaymentMethod.Cash,
            };
            var newer = new PaymentTransaction
            {
                InvoiceId = invoice.Id, PaymentAccountId = invoice.PaymentAccountId, Amount = 600,
                Currency = invoice.Currency, Status = TransactionStatus.Pending,
                GatewayTransactionId = $"CASH-{Guid.NewGuid():N}", Method = PaymentMethod.Cash,
            };
            _db.Context.PaymentTransactions.AddRange(older, newer);
            await _db.Context.SaveChangesAsync();

            await billing.ConfirmCashIntentAsync(newer.Id, new ConfirmCashIntentRequest());

            var (verifyContext, _) = _db.CreateConcurrentSession();
            Assert.Equal(TransactionStatus.Success, (await verifyContext.PaymentTransactions.FirstAsync(t => t.Id == newer.Id)).Status);
            var swept = await verifyContext.PaymentTransactions.FirstAsync(t => t.Id == older.Id);
            Assert.Equal(TransactionStatus.Failed, swept.Status);
            Assert.Contains("settled by another payment", swept.FailureReason!);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task RequestRefund_ConcurrentRequests_MustNotStackBeyondTheTransactionAmount()
        {
            // SCOPE NOTE: as with the other concurrency tests here, SQLite serializes the two
            // contexts onto one connection, so this stages the interleaving rather than racing
            // it — request 2 computes its "already refunded" total from the same committed state
            // request 1 saw. That is exactly the read the check-then-insert depends on; on
            // Postgres two overlapping requests reach it by timing instead.
            var gateway = new FakePaymentGateway();
            var parentUser = await _db.SeedUserAsync($"ref-stack-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();

            var billing1 = CreateBillingService(gateway);
            var invoice = await billing1.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = 1000,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });
            await billing1.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });
            var txn = await _db.Context.PaymentTransactions.AsNoTracking().FirstAsync(t => t.InvoiceId == invoice.Id);

            // A second scoped DbContext, as a concurrent HTTP request would get.
            var (context2, uow2) = _db.CreateConcurrentSession();
            var auditLog2 = new AuditLogService(uow2, _db.CurrentUser);
            var emailTemplates2 = new EmailTemplateService(uow2, auditLog2, new MemoryCache(new MemoryCacheOptions()));
            var notifications2 = new NotificationService(uow2, _emailSender, emailTemplates2, NullLogger<NotificationService>.Instance);
            var billing2 = new BillingService(uow2, auditLog2, gateway, notifications2, _db.CurrentUser);

            var request = () => new RequestRefundRequest
            {
                PaymentTransactionId = txn.Id, Amount = 1000, Reason = "Full refund",
            };

            var task1 = billing1.RequestRefundAsync(request());
            var task2 = billing2.RequestRefundAsync(request());

            try
            {
                await Task.WhenAll(task1, task2);
            }
            catch
            {
                // One of the two being refused is the correct outcome; the assertion below is
                // on the persisted total, not on which request won.
            }

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var live = await verifyContext.Refunds
                .Where(r => r.PaymentTransactionId == txn.Id && r.Status != RefundStatus.Rejected)
                .ToListAsync();
            var total = live.Sum(r => r.Amount);
            verifyContext.Dispose();
            context2.Dispose();

            // The ceiling RequestRefundAsync exists to enforce. Two live refunds of 1000 against
            // one 1000 transaction are each individually approvable, and ReviewRefundAsync's
            // atomic claim is per-refund-row, so it would disburse 2000 against 1000 collected.
            Assert.True(
                total <= txn.Amount,
                $"Refund requests stacked past the transaction: {live.Count} live refund(s) totalling {total} against a {txn.Amount} payment.");
        }

        // ---- QA pass: persisted data consistency after multi-step operations ----

        [Fact]
        public async Task ApproveEnrollment_PersistsAnInternallyConsistentChildSubscriptionAndInvoice()
        {
            // The existing coverage asserts the three rows exist. This asserts they actually
            // hang together once committed — read back through a fresh context, so nothing is
            // satisfied by the seeding context's change tracker.
            var actingAdmin = await _db.SeedUserAsync($"admin-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            _db.CurrentUser.UserId = actingAdmin.Id;

            var parentUser = await _db.SeedUserAsync($"p-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var plan = new PackagePlan
            {
                Name = "Phonics Monthly", BillingType = BillingType.Subscription,
                BillingCycle = BillingCycle.Monthly, Price = 2500,
            };
            var account = new PaymentAccount
            {
                Name = "Phonics", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p",
            };
            _db.Context.AddRange(parentProfile, plan, account);
            await _db.Context.SaveChangesAsync();

            var service = CreateEnrollmentService();
            await service.SubmitAsync(parentUser.Id, new SubmitEnrollmentFormRequest { FormDataJson = "{\"childName\":\"Kid One\",\"dob\":\"2016-01-01\",\"grade\":\"3\",\"courseInterest\":\"Math\"}" });
            var formId = (await service.ListAsync(null)).Single().Id;
            await service.ReviewAsync(formId, new ReviewEnrollmentFormRequest
            {
                Approve = true, ChildFirstName = "Kid", ChildLastName = "One",
                ChildDateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-8), PackagePlanId = plan.Id,
            });

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var child = await verifyContext.Children.SingleAsync();
            var subscription = await verifyContext.Subscriptions.SingleAsync();
            var invoice = await verifyContext.Invoices.SingleAsync();

            // Every foreign key points at the row it claims to.
            Assert.Equal(parentProfile.Id, child.ParentProfileId);
            Assert.Equal(parentProfile.Id, subscription.ParentProfileId);
            Assert.Equal(child.Id, subscription.ChildId);
            Assert.Equal(plan.Id, subscription.PackagePlanId);
            Assert.Equal(parentProfile.Id, invoice.ParentProfileId);
            Assert.Equal(child.Id, invoice.ChildId);
            Assert.Equal(subscription.Id, invoice.SubscriptionId);
            Assert.Equal(account.Id, invoice.PaymentAccountId); // routed to the department's account

            // Money and billing pointers agree with the plan.
            Assert.Equal(plan.Price, invoice.Amount);
            Assert.Equal(0m, invoice.AmountPaid);
            Assert.Equal(InvoiceStatus.Pending, invoice.Status);
            Assert.False(string.IsNullOrWhiteSpace(invoice.InvoiceNumber));
            Assert.Equal(SubscriptionStatus.Active, subscription.Status);
            Assert.NotNull(subscription.NextBillingAtUtc);
            Assert.True(subscription.NextBillingAtUtc > DateTime.UtcNow, "the first renewal must be in the future");

            // Audit fields on the AuditEntity rows are actually stamped, not left default.
            Assert.NotEqual(default, invoice.CreatedAtUtc);
            Assert.Equal(actingAdmin.Id, invoice.CreatedBy);
            Assert.Equal(actingAdmin.Id, subscription.CreatedBy);
            Assert.False(invoice.IsDeleted);

            // The approved form is linked to the child it created.
            var form = await verifyContext.EnrollmentForms.SingleAsync(f => f.Id == formId);
            Assert.Equal(EnrollmentFormStatus.Approved, form.Status);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task SoftDeletedChild_IsExcludedFromParentDashboardAndBatchAssignment()
        {
            var parentUser = await _db.SeedUserAsync($"sd-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.Add(parentProfile);
            await _db.Context.SaveChangesAsync();

            var kept = new Child { ParentProfileId = parentProfile.Id, FirstName = "Kept", LastName = "Child" };
            var removed = new Child { ParentProfileId = parentProfile.Id, FirstName = "Removed", LastName = "Child" };
            _db.Context.Children.AddRange(kept, removed);
            await _db.Context.SaveChangesAsync();

            // Repository.Remove is transparently converted to a soft delete by the interceptor.
            _db.UnitOfWork.Repository<Child>().Remove(removed);
            await _db.UnitOfWork.SaveChangesAsync();

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var stored = await verifyContext.Children.IgnoreQueryFilters().SingleAsync(c => c.Id == removed.Id);
            Assert.True(stored.IsDeleted);
            Assert.NotNull(stored.DeletedAtUtc); // the row survives for history, it is not erased
            verifyContext.Dispose();

            // The global query filter must keep it out of everything user-facing.
            var dashboard = await new ParentPortalService(_db.UnitOfWork).GetDashboardAsync(parentUser.Id);
            var only = Assert.Single(dashboard.Children);
            Assert.Equal(kept.Id, only.ChildId);
        }

        // ---- QA pass: cross-account isolation on the parent portal ----

        [Fact]
        public async Task ParentPortal_NeverLeaksAnotherParentsInvoicesOrRecordings()
        {
            var (billingA, invoiceA) = await SeedInvoiceAsync(amount: 1000);
            var parentAUserId = await _db.Context.ParentProfiles.AsNoTracking()
                .Where(p => p.Id == invoiceA.ParentProfileId).Select(p => p.UserId).FirstAsync();

            var intruderUser = await _db.SeedUserAsync($"intruder-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = intruderUser.Id });
            await _db.Context.SaveChangesAsync();

            var portal = new ParentPortalService(_db.UnitOfWork);

            // Listing is scoped to the caller's own profile.
            Assert.Empty(await portal.GetInvoicesAsync(intruderUser.Id));
            Assert.Single(await portal.GetInvoicesAsync(parentAUserId));

            // And every id-keyed read/write on someone else's invoice is refused, not served.
            await Assert.ThrowsAsync<NotFoundException>(() => billingA.GetParentInvoiceAsync(intruderUser.Id, invoiceA.Id));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                billingA.InitiateParentPaymentAsync(intruderUser.Id, invoiceA.Id, new InitiateParentPaymentRequest { MethodKey = "cash" }));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                billingA.StartParentInlineCheckoutAsync(intruderUser.Id, invoiceA.Id, new InitiateParentPaymentRequest { MethodKey = "upi" }));
            await Assert.ThrowsAsync<NotFoundException>(() => billingA.ReconcileInvoicePaymentAsync(intruderUser.Id, invoiceA.Id));

            // Recordings of a class the intruder's child is not enrolled in.
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            await CreateSessionService().AddRecordingAsync(session.Id, new RegisterRecordingRequest
            {
                StorageUrl = "https://recordings.test/private.mp4", DurationSeconds = 600,
            });
            await Assert.ThrowsAsync<NotFoundException>(() => portal.GetRecordingsAsync(intruderUser.Id, session.Id));
        }

        // ---- QA pass: input validation at the service boundary ----

        [Fact]
        public async Task RecordPayment_RejectsNonPositiveAndOverpayingAmounts()
        {
            var (billing, invoice) = await SeedInvoiceAsync(amount: 1000);

            // Overpayment is already refused; the boundary cases are the interesting ones.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000.01m }));

            await billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1000 });

            // A settled invoice takes no further payment at all.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                billing.RecordPaymentAsync(invoice.Id, new RecordPaymentRequest { Amount = 1 }));

            var (verifyContext, _) = _db.CreateConcurrentSession();
            var settled = await verifyContext.Invoices.FirstAsync(i => i.Id == invoice.Id);
            Assert.Equal(1000m, settled.AmountPaid);
            Assert.NotNull(settled.PaidAtUtc);
            verifyContext.Dispose();
        }

        [Fact]
        public async Task SubmitLeave_RejectsInvertedAndZeroLengthWindows()
        {
            var teacherUser = await _db.SeedUserAsync($"lv-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            _db.Context.TeacherProfiles.Add(new TeacherProfile { UserId = teacherUser.Id });
            await _db.Context.SaveChangesAsync();

            var ops = CreateAcademicOpsService();
            var start = DateTime.UtcNow.AddDays(5);

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                ops.SubmitLeaveAsync(teacherUser.Id, new SubmitLeaveRequest
                {
                    StartAtUtc = start, EndAtUtc = start.AddDays(-1), Reason = "Inverted window",
                }));

            await Assert.ThrowsAsync<DomainValidationException>(() =>
                ops.SubmitLeaveAsync(teacherUser.Id, new SubmitLeaveRequest
                {
                    StartAtUtc = start, EndAtUtc = start, Reason = "Zero-length window",
                }));

            Assert.Empty(await _db.Context.LeaveRequests.ToListAsync());
        }

        [Fact]
        public async Task Gamification_RejectsSelfMintedMilestonesAndNonParticipants()
        {
            var (_, _, session) = await SeedBatchWithSessionAsync(totalSessions: 1);
            var outsiderUser = await _db.SeedUserAsync($"out-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            _db.Context.ParentProfiles.Add(new ParentProfile { UserId = outsiderUser.Id });
            await _db.Context.SaveChangesAsync();

            var gamification = CreateGamificationService();

            // A client must never be able to mint a milestone directly — they are server-computed.
            await Assert.ThrowsAsync<DomainValidationException>(() =>
                gamification.GrantAsync(outsiderUser.Id, new GrantAwardRequest
                {
                    SessionId = session.Id, ParticipantName = "Kid", Kind = AwardKind.Milestone, Points = 0,
                }));

            // A parent with no child in this batch is not a participant.
            await Assert.ThrowsAsync<ForbiddenException>(() =>
                gamification.GrantAsync(outsiderUser.Id, new GrantAwardRequest
                {
                    SessionId = session.Id, ParticipantName = "Kid", Kind = AwardKind.Star, Points = 1,
                }));

            // Nor may a non-staff caller award outside any session at all.
            await Assert.ThrowsAsync<ForbiddenException>(() =>
                gamification.GrantAsync(outsiderUser.Id, new GrantAwardRequest
                {
                    ParticipantName = "Kid", Kind = AwardKind.Star, Points = 1,
                }));

            Assert.Empty(await _db.Context.StudentAwards.ToListAsync());
        }

        /// <summary>A parent with a payment account and one open invoice, plus a live BillingService.</summary>
        private async Task<(BillingService Billing, Invoice Invoice)> SeedInvoiceAsync(decimal amount)
        {
            var parentUser = await _db.SeedUserAsync($"inv-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.AddRange(parentProfile,
                new PaymentAccount { Name = "P", Department = Department.Phonics, GatewayProvider = "t", GatewayAccountRef = "p" });
            await _db.Context.SaveChangesAsync();

            var billing = CreateBillingService();
            var dto = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                ParentProfileId = parentProfile.Id, Department = Department.Phonics, Amount = amount,
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            });
            return (billing, await _db.Context.Invoices.AsNoTracking().FirstAsync(i => i.Id == dto.Id));
        }

        // ---- QA pass: input validation on financially-sensitive fields ----

        [Fact]
        public async Task PayoutRate_RejectsNegativeRateAndOutOfRangeNoShowPenalty()
        {
            var payouts = CreatePayoutService();
            var effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

            // A negative per-session rate makes every completed class DEDUCT from the teacher.
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 45, RatePerSession = -500, EffectiveFrom = effectiveFrom,
            }));

            // A negative penalty percent inverts the sign of the no-show deduction
            // (-(rate * -100 / 100) = +rate), turning a missed class into a BONUS.
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 45, RatePerSession = 500, TeacherNoShowPenaltyPercent = -100, EffectiveFrom = effectiveFrom,
            }));

            // Deducting many times the session's worth is not a configuration, it's a typo.
            await Assert.ThrowsAsync<DomainValidationException>(() => payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 45, RatePerSession = 500, TeacherNoShowPenaltyPercent = 10_000, EffectiveFrom = effectiveFrom,
            }));

            // The legitimate range still saves: 0% is a warning-only no-show, and >100% stays
            // allowed on purpose — deducting more than the session was worth is a supported
            // policy (WBS p.31), which is why the guard bounds the sign and typos, not the policy.
            var saved = await payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 45, RatePerSession = 500, TeacherNoShowPenaltyPercent = 0, EffectiveFrom = effectiveFrom,
            });
            Assert.Equal(0m, saved.TeacherNoShowPenaltyPercent);

            var punitive = await payouts.SetRateAsync(new SavePayoutRateRequest
            {
                DurationMinutes = 60, RatePerSession = 500, TeacherNoShowPenaltyPercent = 150, EffectiveFrom = effectiveFrom,
            });
            Assert.Equal(150m, punitive.TeacherNoShowPenaltyPercent);
        }

        private static RecordEngagementRequest EngagementRequest() => new()
        {
            Events = [new EngagementEntryDto { ParticipantName = "Tester", Type = EngagementEventType.HandRaise }],
        };

        private async Task<(Batch Batch, Course Course, ClassSession Session)> SeedBatchWithSessionAsync(
            int totalSessions,
            bool includeSession = true)
        {
            var teacherUser = await _db.SeedUserAsync($"t-{Guid.NewGuid():N}@test.com", "x", UserRole.Teacher);
            var teacher = new TeacherProfile { UserId = teacherUser.Id };
            var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", Department = Department.Phonics };
            var course = new Course
            {
                CourseCategory = category,
                Name = "Course",
                Type = CourseType.Group,
                DurationMinutes = 45,
                Price = 100,
                TotalSessions = totalSessions,
                Department = Department.Phonics,
            };
            var batch = new Batch { Course = course, TeacherProfile = teacher, Name = "Batch", Capacity = 5 };
            _db.Context.AddRange(teacher, category, course, batch);

            ClassSession session = null!;
            if (includeSession)
            {
                session = new ClassSession
                {
                    Batch = batch,
                    TeacherProfile = teacher,
                    ScheduledStartAtUtc = DateTime.UtcNow.AddDays(1),
                    ScheduledEndAtUtc = DateTime.UtcNow.AddDays(1).AddMinutes(45),
                };
                _db.Context.Add(session);
            }

            await _db.Context.SaveChangesAsync();

            // Session write/read paths (complete, no-show, attendance, recordings, engagement)
            // are scoped to the session's own teacher, so the acting user defaults to exactly
            // that teacher here — the realistic caller. Tests that need a different actor
            // (an unrelated teacher, a parent) overwrite this after seeding.
            _db.CurrentUser.UserId = teacherUser.Id;
            return (batch, course, session);
        }

        // ---- QA round 7: regression coverage ----

        /// <summary>
        /// BUG-001. A cancelled subscription whose child has since been re-subscribed to the
        /// same plan cannot be renewed — that would be a second Active row for the same
        /// child+plan, which CreateSubscriptionAsync forbids and the DB's partial unique index
        /// blocks. RenewSubscriptionAsync made neither check, so the index violation escaped as
        /// a raw DbUpdateException (HTTP 500) instead of the 409 the admin should have seen.
        /// </summary>
        [Fact]
        public async Task RenewSubscription_Conflicts_WhenChildAlreadyHasAnActiveSubscriptionOnThatPlan()
        {
            var (parentProfile, child, plan) = await SeedSubscriptionFixtureAsync();
            var billing = CreateBillingService();

            var first = await billing.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                ParentProfileId = parentProfile.Id,
                ChildId = child.Id,
                PackagePlanId = plan.Id,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            await billing.CancelSubscriptionAsync(first.Id);

            // Free to re-subscribe now that the first one is Cancelled.
            var second = await billing.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                ParentProfileId = parentProfile.Id,
                ChildId = child.Id,
                PackagePlanId = plan.Id,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            Assert.Equal(SubscriptionStatus.Active, second.Status);

            await Assert.ThrowsAsync<ConflictException>(() => billing.RenewSubscriptionAsync(first.Id));

            // The rejected renewal must leave nothing behind: the old subscription stays
            // Cancelled and — critically — no renewal invoice was raised for it.
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var stored = await context.Subscriptions.FirstAsync(s => s.Id == first.Id);
                Assert.Equal(SubscriptionStatus.Cancelled, stored.Status);
                Assert.Null(stored.NextBillingAtUtc);
                // Only the invoice raised when it was first created — the rejected renewal
                // must not have billed the parent for a cycle that never restarted.
                Assert.Equal(1, await context.Invoices.CountAsync(i => i.SubscriptionId == first.Id));
            }
        }

        /// <summary>A genuinely renewable subscription still renews — the new guard must not block the happy path.</summary>
        [Fact]
        public async Task RenewSubscription_StillRenews_WhenNoOtherActiveSubscriptionExists()
        {
            var (parentProfile, child, plan) = await SeedSubscriptionFixtureAsync();
            var billing = CreateBillingService();

            var subscription = await billing.CreateSubscriptionAsync(new CreateSubscriptionRequest
            {
                ParentProfileId = parentProfile.Id,
                ChildId = child.Id,
                PackagePlanId = plan.Id,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            await billing.CancelSubscriptionAsync(subscription.Id);

            var renewed = await billing.RenewSubscriptionAsync(subscription.Id);

            Assert.Equal(SubscriptionStatus.Active, renewed.Status);
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var stored = await context.Subscriptions.FirstAsync(s => s.Id == subscription.Id);
                Assert.Equal(SubscriptionStatus.Active, stored.Status);
                Assert.Null(stored.CancelledAtUtc);
                Assert.NotNull(stored.NextBillingAtUtc);
                // One invoice from the original start, one from the renewal.
                Assert.Equal(2, await context.Invoices.CountAsync(i => i.SubscriptionId == subscription.Id));
            }
        }

        /// <summary>
        /// BUG-002. AppSetting.Key is uniquely indexed, so the same key twice in one bulk
        /// upsert inserted two colliding rows and failed at SaveChanges as a 500 — taking every
        /// other setting in the same request with it.
        /// </summary>
        [Fact]
        public async Task UpsertSettings_RejectsADuplicateKeyInTheSamePayload_WithoutPartiallySaving()
        {
            var settings = new SettingsService(_db.UnitOfWork, _auditLog);

            await Assert.ThrowsAsync<DomainValidationException>(() => settings.UpsertAsync(
            [
                new UpdateSettingRequest { Key = "brand.name", Value = "First", Category = SettingCategory.Branding },
                new UpdateSettingRequest { Key = "brand.name", Value = "Second", Category = SettingCategory.Branding },
                new UpdateSettingRequest { Key = "brand.colour", Value = "#fff", Category = SettingCategory.Branding },
            ]));

            // Validation runs before anything is staged, so the unrelated third key must not
            // have been written either — a half-applied settings save is worse than none.
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                Assert.False(await context.AppSettings.AnyAsync(s => s.Key == "brand.name"));
                Assert.False(await context.AppSettings.AnyAsync(s => s.Key == "brand.colour"));
            }
        }

        /// <summary>
        /// BUG-002 (same fix). UpdateSettingRequest carries no length attributes, so a key or
        /// value longer than the column would pass model validation and fail as a 500 on
        /// Postgres. Both are now bounded in the service, matching the entity.
        /// </summary>
        [Fact]
        public async Task UpsertSettings_RejectsAnOverLongKeyOrValue()
        {
            var settings = new SettingsService(_db.UnitOfWork, _auditLog);

            await Assert.ThrowsAsync<DomainValidationException>(() => settings.UpsertAsync(
                [new UpdateSettingRequest { Key = new string('k', 101), Value = "x", Category = SettingCategory.General }]));

            await Assert.ThrowsAsync<DomainValidationException>(() => settings.UpsertAsync(
                [new UpdateSettingRequest { Key = "brand.blurb", Value = new string('v', 2001), Category = SettingCategory.General }]));

            // Exactly at the limit is still accepted — the guard bounds, it doesn't over-reject.
            var saved = await settings.UpsertAsync(
                [new UpdateSettingRequest { Key = new string('k', 100), Value = new string('v', 2000), Category = SettingCategory.General }]);
            Assert.Contains(saved, s => s.Key.Length == 100 && s.Value!.Length == 2000);
        }

        /// <summary>
        /// BUG-003. SubAdminPermission is uniquely indexed on (UserId, Module). RoleService
        /// already rejected a duplicated module in the role-level matrix; the per-user matrix
        /// didn't, so the same module twice failed at SaveChanges as a 500.
        /// </summary>
        [Fact]
        public async Task SetPermissions_RejectsADuplicateModule_WithoutClearingExistingGrants()
        {
            var admin = await _db.SeedUserAsync($"pa-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var sub = await _db.SeedUserAsync($"ps-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            var users = CreateUserService();

            await users.SetPermissionsAsync(sub.Id, admin.Id,
                [new PermissionDto { Module = PermissionModule.Admission, CanView = true }]);

            await Assert.ThrowsAsync<DomainValidationException>(() => users.SetPermissionsAsync(sub.Id, admin.Id,
            [
                new PermissionDto { Module = PermissionModule.Settings, CanView = true },
                new PermissionDto { Module = PermissionModule.Settings, CanEdit = true },
            ]));

            // SetPermissionsAsync is replace-all: rejecting late (at SaveChanges) would have
            // meant the existing grants were already staged for removal. The guard runs first,
            // so the sub-admin keeps the access they had.
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var grants = await context.SubAdminPermissions.Where(p => p.UserId == sub.Id).ToListAsync();
                Assert.Single(grants);
                Assert.Equal(PermissionModule.Admission, grants[0].Module);
                Assert.True(grants[0].CanView);
            }
        }

        /// <summary>
        /// Regression for a68b1a1: the status filter has to compose with paging and with the
        /// parentProfileId filter. The commit's own test only paged a parentProfileId-filtered
        /// set, so the status branch was never proven to survive Skip/Take or to be reflected
        /// in TotalCount (which is counted off a separate query from the page itself).
        /// </summary>
        [Fact]
        public async Task ListInvoices_ComposesTheStatusFilterWithPagingAndTheParentFilter()
        {
            var (mine, _) = await SeedInvoiceOwnerAsync();
            var (other, _) = await SeedInvoiceOwnerAsync();
            var billing = CreateBillingService();

            // 5 invoices for the parent under test, plus 2 for an unrelated parent that must
            // never leak into a parent-filtered page or its TotalCount.
            var minesIds = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
                {
                    ParentProfileId = mine.Id,
                    Department = Department.Phonics,
                    Amount = 100 + i,
                    DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                });
                minesIds.Add(invoice.Id);
            }

            for (var i = 0; i < 2; i++)
            {
                await billing.CreateInvoiceAsync(new CreateInvoiceRequest
                {
                    ParentProfileId = other.Id,
                    Department = Department.Phonics,
                    Amount = 900,
                    DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                });
            }

            // Settle two of this parent's invoices so the two statuses are genuinely mixed.
            await billing.RecordPaymentAsync(minesIds[0], new RecordPaymentRequest
            {
                Amount = 100,
                Method = PaymentMethod.Cash,
            });
            await billing.RecordPaymentAsync(minesIds[1], new RecordPaymentRequest
            {
                Amount = 101,
                Method = PaymentMethod.Cash,
            });

            // Status filter alone: TotalCount counts the filtered set, not the whole table.
            var paid = await billing.ListInvoicesAsync(InvoiceStatus.Paid, null, page: 1, pageSize: 50);
            Assert.Equal(2, paid.TotalCount);
            Assert.All(paid.Items, i => Assert.Equal(InvoiceStatus.Paid, i.Status));

            // Status + parent together, paged: 3 Pending rows for this parent over 2-row pages.
            var firstPage = await billing.ListInvoicesAsync(InvoiceStatus.Pending, mine.Id, page: 1, pageSize: 2);
            Assert.Equal(3, firstPage.TotalCount);
            Assert.Equal(2, firstPage.Items.Count);
            Assert.All(firstPage.Items, i => Assert.Equal(InvoiceStatus.Pending, i.Status));
            Assert.All(firstPage.Items, i => Assert.Equal(mine.Id, i.ParentProfileId));

            var secondPage = await billing.ListInvoicesAsync(InvoiceStatus.Pending, mine.Id, page: 2, pageSize: 2);
            Assert.Single(secondPage.Items);
            Assert.Equal(3, secondPage.TotalCount);

            // The Id tiebreaker has to hold under the filtered query too, not just the
            // unfiltered one — every row appears exactly once across the two pages.
            var paged = firstPage.Items.Concat(secondPage.Items).Select(i => i.Id).ToList();
            Assert.Equal(3, paged.Distinct().Count());
            Assert.DoesNotContain(minesIds[0], paged); // the Paid ones stay filtered out
            Assert.DoesNotContain(minesIds[1], paged);

            // Past the last page is empty, but TotalCount still reports the filtered total.
            var beyond = await billing.ListInvoicesAsync(InvoiceStatus.Pending, mine.Id, page: 3, pageSize: 2);
            Assert.Empty(beyond.Items);
            Assert.Equal(3, beyond.TotalCount);
        }

        /// <summary>
        /// Regression for a68b1a1's clamping. page/pageSize arrive straight off the query
        /// string, so page=0 (a 0-indexed client) or a negative page would otherwise produce
        /// Skip(-n) — which EF rejects at translation time — and pageSize=0 an empty page.
        /// </summary>
        [Fact]
        public async Task ListInvoices_ClampsANonPositivePageOrPageSize()
        {
            var (parentProfile, _) = await SeedInvoiceOwnerAsync();
            var billing = CreateBillingService();
            for (var i = 0; i < 3; i++)
            {
                await billing.CreateInvoiceAsync(new CreateInvoiceRequest
                {
                    ParentProfileId = parentProfile.Id,
                    Department = Department.Phonics,
                    Amount = 100 + i,
                    DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                });
            }

            var pageZero = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 0, pageSize: 2);
            Assert.Equal(1, pageZero.Page);
            Assert.Equal(2, pageZero.Items.Count);

            var negativePage = await billing.ListInvoicesAsync(null, parentProfile.Id, page: -5, pageSize: 2);
            Assert.Equal(1, negativePage.Page);
            Assert.Equal(pageZero.Items.Select(i => i.Id), negativePage.Items.Select(i => i.Id));

            // pageSize floors at 1 rather than returning an empty page for a valid request.
            var zeroSize = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 1, pageSize: 0);
            Assert.Equal(1, zeroSize.PageSize);
            Assert.Single(zeroSize.Items);
            Assert.Equal(3, zeroSize.TotalCount);

            var negativeSize = await billing.ListInvoicesAsync(null, parentProfile.Id, page: 1, pageSize: -10);
            Assert.Equal(1, negativeSize.PageSize);
        }

        /// <summary>
        /// Regression for b35e798. AuditLogService.ListAsync gained a ThenBy(Id) tiebreaker;
        /// the commit proved pages don't overlap, but not that the entityName/action filters
        /// still compose with paging, nor that page/pageSize are clamped the same way.
        /// </summary>
        [Fact]
        public async Task AuditLog_ListAsync_ComposesFiltersWithPaging_AndClampsThePage()
        {
            var actor = await _db.SeedUserAsync($"al-f-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);
            var other = await _db.SeedUserAsync($"al-o-{Guid.NewGuid():N}@test.com", "x", UserRole.Admin);

            // One batched save, so every row ties on CreatedAtUtc — the exact condition the
            // Id tiebreaker exists for, now applied through a filter as well.
            _db.Context.AuditLogs.AddRange(
            [
                .. Enumerable.Range(0, 5).Select(_ => new AuditLog
                {
                    ActorUserId = actor.Id, Action = AuditAction.Update, EntityName = "FilterProbe",
                }),
                .. Enumerable.Range(0, 3).Select(_ => new AuditLog
                {
                    ActorUserId = actor.Id, Action = AuditAction.Delete, EntityName = "FilterProbe",
                }),
                .. Enumerable.Range(0, 4).Select(_ => new AuditLog
                {
                    ActorUserId = other.Id, Action = AuditAction.Update, EntityName = "OtherEntity",
                }),
            ]);
            await _db.Context.SaveChangesAsync();

            // entityName filter, paged: 8 rows over 3-row pages, no overlap and none missing.
            var p1 = await _auditLog.ListAsync("FilterProbe", null, page: 1, pageSize: 3);
            var p2 = await _auditLog.ListAsync("FilterProbe", null, page: 2, pageSize: 3);
            var p3 = await _auditLog.ListAsync("FilterProbe", null, page: 3, pageSize: 3);
            Assert.Equal(8, p1.TotalCount);
            var walked = p1.Items.Concat(p2.Items).Concat(p3.Items).Select(e => e.Id).ToList();
            Assert.Equal(8, walked.Count);
            Assert.Equal(8, walked.Distinct().Count());

            // entityName + action together.
            var deletes = await _auditLog.ListAsync("FilterProbe", AuditAction.Delete, page: 1, pageSize: 50);
            Assert.Equal(3, deletes.TotalCount);
            Assert.All(deletes.Items, e => Assert.Equal(AuditAction.Delete, e.Action));

            // restrictToActorId (what a non-platform-view caller gets) composes too.
            var mineOnly = await _auditLog.ListAsync(null, null, page: 1, pageSize: 50, restrictToActorId: other.Id);
            Assert.All(mineOnly.Items, e => Assert.Equal(other.Id, e.ActorUserId));
            Assert.Equal(4, mineOnly.TotalCount);

            // Same clamping contract as ListInvoicesAsync.
            var clamped = await _auditLog.ListAsync("FilterProbe", null, page: 0, pageSize: 100_000);
            Assert.Equal(1, clamped.Page);
            Assert.Equal(200, clamped.PageSize);
        }

        /// <summary>
        /// BUG-004. SaveIntegrationRequest / SaveRoleRequest / SaveMenuItemRequest /
        /// UpdateSettingRequest carried no length attributes at all, while their entities'
        /// columns are varchar(N). On Postgres an over-long field passed model validation and
        /// blew up at SaveChanges as an unhandled DbUpdateException (a 500) rather than a 400.
        /// Asserted against the annotations directly: SQLite does not enforce varchar length,
        /// so the 500 itself is only reproducible on the real stack.
        /// </summary>
        [Theory]
        [InlineData(typeof(SaveIntegrationRequest), nameof(SaveIntegrationRequest.Key), 64)]
        [InlineData(typeof(SaveIntegrationRequest), nameof(SaveIntegrationRequest.Name), 100)]
        [InlineData(typeof(SaveIntegrationRequest), nameof(SaveIntegrationRequest.Description), 500)]
        [InlineData(typeof(SaveRoleRequest), nameof(SaveRoleRequest.Name), 64)]
        [InlineData(typeof(SaveRoleRequest), nameof(SaveRoleRequest.DisplayName), 100)]
        [InlineData(typeof(SaveRoleRequest), nameof(SaveRoleRequest.Description), 500)]
        [InlineData(typeof(SaveRoleRequest), nameof(SaveRoleRequest.DefaultRoute), 200)]
        [InlineData(typeof(SaveMenuItemRequest), nameof(SaveMenuItemRequest.Portal), 32)]
        [InlineData(typeof(SaveMenuItemRequest), nameof(SaveMenuItemRequest.Section), 64)]
        [InlineData(typeof(SaveMenuItemRequest), nameof(SaveMenuItemRequest.Label), 100)]
        [InlineData(typeof(SaveMenuItemRequest), nameof(SaveMenuItemRequest.Path), 200)]
        [InlineData(typeof(SaveMenuItemRequest), nameof(SaveMenuItemRequest.Icon), 64)]
        [InlineData(typeof(UpdateSettingRequest), nameof(UpdateSettingRequest.Key), 100)]
        [InlineData(typeof(UpdateSettingRequest), nameof(UpdateSettingRequest.Value), 2000)]
        public void AdminSaveRequests_BoundEveryStringFieldToItsColumnLength(
            Type requestType, string propertyName, int expectedMaxLength)
        {
            var property = requestType.GetProperty(propertyName)!;
            var maxLength = property
                .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), false)
                .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()
                .SingleOrDefault();

            Assert.True(maxLength is not null, $"{requestType.Name}.{propertyName} has no [MaxLength].");
            Assert.Equal(expectedMaxLength, maxLength!.Length);
            Assert.False(maxLength.IsValid(new string('x', expectedMaxLength + 1)));
            Assert.True(maxLength.IsValid(new string('x', expectedMaxLength)));
        }

        [Fact]
        public async Task ResetPin_ReturnsAWorkingPin_WithoutSendingAnything()
        {
            var user = await _db.SeedUserAsync($"resetpin-{Guid.NewGuid():N}@test.com", "old-pin", UserRole.Parent);
            var originalHash = user.PinHash;

            var temporaryPin = await CreateUserService().ResetPinAsync(user.Id);

            Assert.False(string.IsNullOrWhiteSpace(temporaryPin));
            var (verifyContext, _) = _db.CreateConcurrentSession();
            var stored = await verifyContext.Users.FirstAsync(u => u.Id == user.Id);
            Assert.NotEqual(originalHash, stored.PinHash); // a real new PIN was set, not a no-op
            Assert.True(_hasher.Verify(temporaryPin, stored.PinHash)); // and it's the one actually returned
            Assert.False(_hasher.Verify("old-pin", stored.PinHash)); // the old PIN no longer works
            verifyContext.Dispose();
        }

        /// <summary>
        /// CourseService.UpdateAsync's two structural guards, neither previously covered:
        /// a course backing a multi-student batch can't become Individual, and TotalSessions /
        /// DurationMinutes can't move once a schedule has been generated from them.
        /// </summary>
        [Fact]
        public async Task UpdateCourse_RefusesToDesyncAnAlreadyGeneratedScheduleOrAMultiStudentBatch()
        {
            var (batch, course, _) = await SeedBatchWithSessionAsync(totalSessions: 4);
            var courses = CreateCourseService();

            Task<CourseDto> Save(CourseType type, int totalSessions, int durationMinutes) =>
                courses.UpdateAsync(course.Id, new SaveCourseRequest
                {
                    CourseCategoryId = course.CourseCategoryId,
                    Name = course.Name,
                    Type = type,
                    DurationMinutes = durationMinutes,
                    Price = course.Price,
                    TotalSessions = totalSessions,
                    Department = course.Department,
                    IsActive = true,
                });

            // A session already exists for this batch, so the schedule is generated: neither
            // TotalSessions nor DurationMinutes may move.
            await Assert.ThrowsAsync<DomainValidationException>(() => Save(CourseType.Group, 6, 45));
            await Assert.ThrowsAsync<DomainValidationException>(() => Save(CourseType.Group, 4, 60));

            // Leaving both alone is fine — the guard is about desync, not about editing at all.
            var renamed = await Save(CourseType.Group, 4, 45);
            Assert.Equal(CourseType.Group, renamed.Type);

            // Two active students in the batch blocks the switch to Individual (which would
            // otherwise bypass BatchService's own one-student-per-Individual-batch rule).
            var parentProfile = new ParentProfile
            {
                UserId = (await _db.SeedUserAsync($"cp-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent)).Id,
            };
            _db.Context.Add(parentProfile);
            var children = Enumerable.Range(0, 2)
                .Select(i => new Child { ParentProfile = parentProfile, FirstName = $"Kid{i}", LastName = "X" })
                .ToList();
            _db.Context.AddRange(children);
            _db.Context.AddRange(children.Select(c => new BatchEnrollment
            {
                BatchId = batch.Id,
                Child = c,
                Status = EnrollmentStatus.Active,
            }));
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<DomainValidationException>(() => Save(CourseType.Individual, 4, 45));

            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var stored = await context.Courses.FirstAsync(c => c.Id == course.Id);
                Assert.Equal(CourseType.Group, stored.Type); // nothing partially applied
                Assert.Equal(4, stored.TotalSessions);
                Assert.Equal(45, stored.DurationMinutes);
            }
        }

        /// <summary>
        /// BatchService.SetStatusAsync's side effects, previously uncovered: taking a batch
        /// out of service must cancel its still-scheduled future sessions and expire the
        /// subscriptions that were paying for the course it just stopped running.
        /// </summary>
        [Fact]
        public async Task SetBatchDormant_CancelsFutureSessions_AndExpiresTheSubscriptionsPayingForIt()
        {
            var (batch, course, futureSession) = await SeedBatchWithSessionAsync(totalSessions: 4);

            var parentUser = await _db.SeedUserAsync($"bs-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "X" };
            var plan = new PackagePlan
            {
                Name = "Monthly",
                Course = course,
                BillingType = BillingType.Subscription,
                BillingCycle = BillingCycle.Monthly,
                Price = 1000,
            };
            var subscription = new Subscription
            {
                ParentProfile = parentProfile,
                Child = child,
                PackagePlan = plan,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Status = SubscriptionStatus.Active,
                NextBillingAtUtc = DateTime.UtcNow.AddDays(30),
            };
            // A session already in the past must be left alone — only future ones are dangling.
            var pastSession = new ClassSession
            {
                BatchId = batch.Id,
                TeacherProfileId = batch.TeacherProfileId,
                ScheduledStartAtUtc = DateTime.UtcNow.AddDays(-3),
                ScheduledEndAtUtc = DateTime.UtcNow.AddDays(-3).AddMinutes(45),
                Status = SessionStatus.Scheduled,
            };
            _db.Context.AddRange(parentProfile, child, plan, subscription, pastSession);
            _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, Child = child, Status = EnrollmentStatus.Active });
            await _db.Context.SaveChangesAsync();

            await CreateBatchService().SetStatusAsync(batch.Id, BatchStatus.Dormant);

            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var stored = await context.Batches.FirstAsync(b => b.Id == batch.Id);
                Assert.Equal(BatchStatus.Dormant, stored.Status);
                Assert.NotNull(stored.CompletedAtUtc);

                var future = await context.ClassSessions.FirstAsync(s => s.Id == futureSession.Id);
                Assert.Equal(SessionStatus.Cancelled, future.Status);
                Assert.Contains("Dormant", future.CancellationReason!);

                var past = await context.ClassSessions.FirstAsync(s => s.Id == pastSession.Id);
                Assert.Equal(SessionStatus.Scheduled, past.Status); // history untouched

                var storedSubscription = await context.Subscriptions.FirstAsync(s => s.Id == subscription.Id);
                Assert.Equal(SubscriptionStatus.Expired, storedSubscription.Status);
                Assert.Null(storedSubscription.NextBillingAtUtc); // and the billing job won't re-invoice
            }
        }

        /// <summary>
        /// IntegrationService's secret handling, previously untested and security-relevant:
        /// gateway credentials must never round-trip to the client in the clear, and an admin
        /// saving the form back unchanged must not overwrite the real secret with its mask.
        /// </summary>
        [Fact]
        public async Task Integration_MasksSecretsOnRead_AndPreservesThemWhenTheMaskIsSavedBack()
        {
            var integrations = new IntegrationService(_db.UnitOfWork, _auditLog);

            var created = await integrations.CreateAsync(new SaveIntegrationRequest
            {
                Key = "Razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = true,
                Config = new Dictionary<string, string?>
                {
                    ["apiKey"] = "rzp_live_supersecret1234",
                    ["apiSecret"] = "shhh_abcd",
                    ["webhookUrl"] = "https://hooks.test/rzp",
                },
            });

            Assert.Equal("razorpay", created.Key); // normalized to lower-case
            // All but the last 4 characters bulleted — "rzp_live_supersecret1234" is 24 long.
            Assert.Equal(new string('•', 20) + "1234", created.Config["apiKey"]);
            Assert.Equal("•••••abcd", created.Config["apiSecret"]);
            Assert.Equal("https://hooks.test/rzp", created.Config["webhookUrl"]); // not a secret field

            // The admin edits only the webhook and posts the form back — the secret fields
            // still hold the masks they were rendered with.
            var updated = await integrations.UpdateAsync(created.Id, new SaveIntegrationRequest
            {
                Key = "razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = true,
                Config = new Dictionary<string, string?>
                {
                    ["apiKey"] = created.Config["apiKey"],
                    ["apiSecret"] = created.Config["apiSecret"],
                    ["webhookUrl"] = "https://hooks.test/rzp-v2",
                },
            });

            Assert.Equal("https://hooks.test/rzp-v2", updated.Config["webhookUrl"]);

            // The stored ciphertext must still be the original, not the bullet string.
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                var stored = await context.Integrations.FirstAsync(i => i.Id == created.Id);
                Assert.Contains("rzp_live_supersecret1234", stored.ConfigJson!);
                Assert.Contains("shhh_abcd", stored.ConfigJson!);
                Assert.DoesNotContain("•", stored.ConfigJson!);
            }

            // A genuinely new secret still replaces the old one.
            var rotated = await integrations.UpdateAsync(created.Id, new SaveIntegrationRequest
            {
                Key = "razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = true,
                Config = new Dictionary<string, string?> { ["apiSecret"] = "rotated_wxyz" },
            });
            Assert.Equal("••••••••wxyz", rotated.Config["apiSecret"]);
        }

        /// <summary>
        /// The Configure dialog already warns client-side when a Razorpay Key Id doesn't look
        /// right (most likely the Key Secret pasted into the wrong field) — but nothing
        /// stopped the bad value from actually being saved. Confirmed live: production has an
        /// integration saved with exactly this mixup.
        /// </summary>
        [Fact]
        public async Task Integration_RejectsRazorpayKeyId_ThatDoesNotStartWithRzp()
        {
            var integrations = new IntegrationService(_db.UnitOfWork, _auditLog);

            await Assert.ThrowsAsync<DomainValidationException>(() => integrations.CreateAsync(new SaveIntegrationRequest
            {
                Key = "razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = true,
                Config = new Dictionary<string, string?>
                {
                    // Looks like a Key Secret was pasted into the Key Id field.
                    ["apiKey"] = "shhh_this_is_actually_the_secret",
                    ["apiSecret"] = "rzp_live_realkeyid",
                },
            }));

            // A valid keyId still saves fine — this isn't rejecting Razorpay integrations
            // outright, only a value that can't be a real Key Id.
            var created = await integrations.CreateAsync(new SaveIntegrationRequest
            {
                Key = "razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = true,
                Config = new Dictionary<string, string?> { ["apiKey"] = "rzp_live_valid1234" },
            });
            Assert.NotEqual(Guid.Empty, created.Id);

            // An update that touches an unrelated field (the keyId's mask round-trips
            // untouched) must not be rejected as if it were a fresh invalid value.
            var updated = await integrations.UpdateAsync(created.Id, new SaveIntegrationRequest
            {
                Key = "razorpay",
                Name = "Razorpay",
                Category = IntegrationCategory.PaymentGateway,
                IsEnabled = false,
                Config = new Dictionary<string, string?> { ["apiKey"] = created.Config["apiKey"] },
            });
            Assert.False(updated.IsEnabled);
        }

        /// <summary>
        /// RoleService's system-role and in-use protections, previously untested. A seeded role
        /// backs the Sub Admin preset flow, so renaming or deleting one would strand every user
        /// assigned to it.
        /// </summary>
        [Fact]
        public async Task RoleService_ProtectsSystemRoles_AndRefusesToDeleteOneStillAssigned()
        {
            var roles = new RoleService(_db.UnitOfWork, _auditLog);

            var systemRole = new RoleDefinition
            {
                Name = "academic-coordinator",
                DisplayName = "Academic Coordinator",
                DefaultRoute = "/coordinator",
                IsSystem = true,
            };
            _db.Context.Add(systemRole);
            await _db.Context.SaveChangesAsync();
            // RoleService loads no-tracking and calls Update(); production hands each request
            // its own DbContext, so the seed instance must not stay tracked here.
            _db.Context.ChangeTracker.Clear();

            SaveRoleRequest Request(string name, string? route = "/coordinator") => new()
            {
                Name = name,
                DisplayName = "Academic Coordinator",
                DefaultRoute = route,
                Permissions = [new PermissionDto { Module = PermissionModule.Admission, CanView = true }],
            };

            // Each call stands for its own HTTP request, so each gets a clean tracker.
            Task<RoleDto> Update(SaveRoleRequest request)
            {
                _db.Context.ChangeTracker.Clear();
                return roles.UpdateAsync(systemRole.Id, request);
            }

            await Assert.ThrowsAsync<DomainValidationException>(() => Update(Request("renamed-coordinator")));

            _db.Context.ChangeTracker.Clear();
            await Assert.ThrowsAsync<DomainValidationException>(() => roles.DeleteAsync(systemRole.Id));

            // A system role's permission matrix is still editable — that's the whole point of
            // the preset editor; only its identity is frozen.
            var edited = await Update(Request("academic-coordinator"));
            Assert.Single(edited.Permissions);

            // A route not starting with '/' is rejected, and a duplicated module is too.
            await Assert.ThrowsAsync<DomainValidationException>(
                () => Update(Request("academic-coordinator", "coordinator")));
            await Assert.ThrowsAsync<DomainValidationException>(() => Update(new SaveRoleRequest
            {
                Name = "academic-coordinator",
                DisplayName = "Academic Coordinator",
                Permissions =
                [
                    new PermissionDto { Module = PermissionModule.Admission, CanView = true },
                    new PermissionDto { Module = PermissionModule.Admission, CanEdit = true },
                ],
            }));
            _db.Context.ChangeTracker.Clear();

            // A custom role in use by a Sub Admin can't be deleted out from under them.
            var custom = await roles.CreateAsync(new SaveRoleRequest
            {
                Name = "Front-Desk",
                DisplayName = "Front Desk",
                Permissions = [],
            });
            Assert.Equal("front-desk", custom.Name);
            await Assert.ThrowsAsync<ConflictException>(() => roles.CreateAsync(new SaveRoleRequest
            {
                Name = "FRONT-DESK",
                DisplayName = "Front Desk Again",
            }));

            var subAdmin = await _db.SeedUserAsync($"rs-{Guid.NewGuid():N}@test.com", "x", UserRole.SubAdmin);
            subAdmin.RoleDefinitionId = custom.Id;
            await _db.Context.SaveChangesAsync();

            await Assert.ThrowsAsync<ConflictException>(() => roles.DeleteAsync(custom.Id));
        }

        [Fact]
        public async Task UpdateRole_CannotSaveAwayARequiredSystemGrant()
        {
            // Reproduces the live bug: saving the "management" preset from the Roles &
            // Permissions screen without the Courses box checked used to wipe
            // CourseBatchManagement:View outright, breaking that role's own Revenue & Courses
            // screen until the process next restarted (the startup-only backfill was the only
            // thing that ever put it back). RoleService.UpdateAsync must not be able to save
            // that grant away, independent of what the submitted matrix says.
            var roles = new RoleService(_db.UnitOfWork, _auditLog);
            var managementRole = new RoleDefinition
            {
                Name = "management",
                DisplayName = "Management",
                DefaultRoute = "/management",
                IsSystem = true,
            };
            _db.Context.Add(managementRole);
            await _db.Context.SaveChangesAsync();
            _db.Context.ChangeTracker.Clear();

            // Admin edits the role for an unrelated reason (adding Reports access) and submits
            // the matrix as the screen actually would — Courses simply isn't in it.
            var updated = await roles.UpdateAsync(managementRole.Id, new SaveRoleRequest
            {
                Name = "management",
                DisplayName = "Management",
                DefaultRoute = "/management",
                Permissions = [new PermissionDto { Module = PermissionModule.ReportsAnalytics, CanView = true }],
            });

            var coursesGrant = Assert.Single(updated.Permissions, p => p.Module == PermissionModule.CourseBatchManagement);
            Assert.True(coursesGrant.CanView);
            Assert.Contains(updated.Permissions, p => p.Module == PermissionModule.ReportsAnalytics && p.CanView);
        }

        /// <summary>
        /// EmailTemplateService's substitution rules, previously exercised only indirectly.
        /// Token values are parent-supplied (child/parent names), so they must be HTML-escaped
        /// in the body and stripped of CR/LF in the subject (mail-header injection).
        /// </summary>
        [Fact]
        public async Task EmailTemplate_EscapesTokenValuesInHtml_AndStripsLineBreaksFromTheSubject()
        {
            var template = new EmailTemplate
            {
                Key = "qa-substitution",
                Name = "QA",
                Category = NotificationType.General,
                Subject = "Welcome {{Name}}",
                HtmlBody = "<p>Hello {{Name}}, see {{Missing}} and {{Note}}</p>",
                PlaceholdersJson = "[\"Name\",\"Note\"]",
                IsActive = true,
            };
            _db.Context.EmailTemplates.Add(template);
            await _db.Context.SaveChangesAsync();

            var (subject, body) = await _emailTemplates.RenderAsync("qa-substitution", new Dictionary<string, string>
            {
                // A name carrying markup, a header-injection attempt, and a value that itself
                // looks like another token (which a naive replace-per-token loop would re-expand).
                ["Name"] = "<script>alert(1)</script>\r\nBcc: attacker@evil.test",
                ["Note"] = "{{Name}}",
            });

            Assert.DoesNotContain("\r", subject);
            Assert.DoesNotContain("\n", subject);
            Assert.Contains("Bcc: attacker@evil.test", subject); // flattened onto one line, not a new header

            Assert.DoesNotContain("<script>", body);
            Assert.Contains("&lt;script&gt;", body);
            // {{Note}}'s value is literally "{{Name}}" — it must survive as text, never be
            // re-substituted with Name's value.
            Assert.Contains("{{Name}}", body);
            Assert.Contains("{{Missing}}", body); // an unsupplied token is left as-is, not blanked

            // An edit invalidates the render cache immediately rather than waiting out the TTL.
            await _emailTemplates.UpdateAsync(template.Id, new SaveEmailTemplateRequest
            {
                Subject = "Updated {{Name}}",
                HtmlBody = "<p>v2 {{Name}}</p>",
                IsActive = true,
            });
            var (afterEdit, afterBody) = await _emailTemplates.RenderAsync(
                "qa-substitution", new Dictionary<string, string> { ["Name"] = "Ann" });
            Assert.Equal("Updated Ann", afterEdit);
            Assert.Contains("v2 Ann", afterBody);

            // Deactivating falls back to the generic message rather than blocking the send.
            await _emailTemplates.UpdateAsync(template.Id, new SaveEmailTemplateRequest
            {
                Subject = "Updated {{Name}}",
                HtmlBody = "<p>v2 {{Name}}</p>",
                IsActive = false,
            });
            var (fallback, _) = await _emailTemplates.RenderAsync(
                "qa-substitution", new Dictionary<string, string> { ["Name"] = "Ann" });
            Assert.Equal("Notification from The Reader Nest", fallback);
        }

        /// <summary>
        /// Bulk email recipient scoping, previously untested. The count shown on the compose
        /// screen has to be exactly who the send reaches, and a batch-scoped send must not
        /// spill into unrelated parents.
        /// </summary>
        [Fact]
        public async Task BulkEmail_PreviewCountMatchesTheSend_AndABatchScopedSendStaysInsideTheBatch()
        {
            var (batch, _, _) = await SeedBatchWithSessionAsync(totalSessions: 2);
            var reports = new ReportsService(_db.UnitOfWork, _notifications);

            async Task<ParentProfile> SeedParentWithChildAsync(bool enrol, UserStatus status = UserStatus.Active)
            {
                var user = await _db.SeedUserAsync($"be-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent, status);
                var profile = new ParentProfile { UserId = user.Id };
                var child = new Child { ParentProfile = profile, FirstName = "Kid", LastName = "X" };
                _db.Context.AddRange(profile, child);
                if (enrol)
                {
                    _db.Context.Add(new BatchEnrollment { BatchId = batch.Id, Child = child, Status = EnrollmentStatus.Active });
                }

                await _db.Context.SaveChangesAsync();
                return profile;
            }

            await SeedParentWithChildAsync(enrol: true);
            await SeedParentWithChildAsync(enrol: true);
            await SeedParentWithChildAsync(enrol: false);          // active, but not in this batch
            await SeedParentWithChildAsync(enrol: false, status: UserStatus.Inactive);

            var batchPreview = await reports.PreviewBulkEmailAsync(batch.Id);
            Assert.Equal(2, batchPreview.RecipientCount);

            _emailSender.Sent.Clear();
            var batchSend = await reports.SendBulkEmailAsync(new BulkEmailRequest
            {
                Subject = "Class update",
                Body = "<p>See you Monday.</p>",
                BatchId = batch.Id,
            });
            Assert.Equal(2, batchSend.RecipientCount);
            Assert.Equal(2, _emailSender.Sent.Count); // the preview count is the real reach

            // Unscoped goes to every ACTIVE parent — the inactive one is excluded, and so is
            // any parent whose account was deactivated after enrolling.
            var allPreview = await reports.PreviewBulkEmailAsync(null);
            Assert.Equal(3, allPreview.RecipientCount);

            _emailSender.Sent.Clear();
            var allSend = await reports.SendBulkEmailAsync(new BulkEmailRequest
            {
                Subject = "Newsletter",
                Body = "<p>Hello</p>",
            });
            Assert.Equal(3, allSend.RecipientCount);
            Assert.Equal(3, _emailSender.Sent.Count);

            // Every send is journalled, so a failed delivery is auditable rather than silent.
            var (context, _) = _db.CreateConcurrentSession();
            using (context)
            {
                Assert.Equal(5, await context.Notifications.CountAsync(n => n.Channel == NotificationChannel.Email
                    && (n.Subject == "Class update" || n.Subject == "Newsletter")));
            }
        }

        private async Task<(ParentProfile Parent, User User)> SeedInvoiceOwnerAsync()
        {
            var parentUser = await _db.SeedUserAsync($"inv-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            _db.Context.Add(parentProfile);
            if (!await _db.Context.PaymentAccounts.AnyAsync(a => a.Department == Department.Phonics))
            {
                _db.Context.Add(new PaymentAccount
                {
                    Name = "Phonics",
                    Department = Department.Phonics,
                    GatewayProvider = "razorpay",
                    GatewayAccountRef = "ph",
                });
            }

            await _db.Context.SaveChangesAsync();
            return (parentProfile, parentUser);
        }

        private async Task<(ParentProfile Parent, Child Child, PackagePlan Plan)> SeedSubscriptionFixtureAsync()
        {
            var parentUser = await _db.SeedUserAsync($"sub-{Guid.NewGuid():N}@test.com", "x", UserRole.Parent);
            var parentProfile = new ParentProfile { UserId = parentUser.Id };
            var child = new Child { ParentProfile = parentProfile, FirstName = "Kid", LastName = "One" };
            var category = new CourseCategory { Name = $"Cat-{Guid.NewGuid():N}", Department = Department.Phonics };
            var course = new Course
            {
                CourseCategory = category,
                Name = "Course",
                Type = CourseType.Group,
                DurationMinutes = 45,
                Price = 100,
                TotalSessions = 8,
                Department = Department.Phonics,
            };
            var plan = new PackagePlan
            {
                Name = "Monthly",
                Course = course,
                BillingType = BillingType.Subscription,
                BillingCycle = BillingCycle.Monthly,
                Price = 1000,
            };
            var account = new PaymentAccount
            {
                Name = "Phonics",
                Department = Department.Phonics,
                GatewayProvider = "razorpay",
                GatewayAccountRef = "ph",
            };
            _db.Context.AddRange(parentProfile, child, category, course, plan, account);
            await _db.Context.SaveChangesAsync();
            return (parentProfile, child, plan);
        }

        /// <summary>
        /// ResourceService.CreateAsync never checked CourseId/BatchId(s) existed before
        /// writing Resource/ResourceBatchVisibility rows referencing them - a stale dropdown
        /// value (or one deleted between page-load and submit) hit the FK constraint at
        /// SaveChanges as an unhandled DbUpdateException, surfacing to the uploader as a raw
        /// 500 with no indication of what was wrong. Reproduced directly against SQLite
        /// (real FK enforcement, same as Postgres) before being fixed.
        /// </summary>
        [Fact]
        public async Task UploadResource_RejectsANonExistentCourseId_WithACleanNotFound()
        {
            var resources = CreateResourceService();
            var ex = await Assert.ThrowsAsync<NotFoundException>(() => resources.CreateAsync(
                new CreateResourceRequest { Title = "R", Type = ResourceType.Worksheet, CourseId = Guid.NewGuid() },
                "some/stored/path.txt", "text/plain", 100));
            Assert.Contains("Course", ex.Message);
            Assert.Empty(await _db.Context.Resources.ToListAsync());
        }

        [Fact]
        public async Task UploadResource_RejectsANonExistentBatchId_WithACleanNotFound()
        {
            var resources = CreateResourceService();
            var ex = await Assert.ThrowsAsync<NotFoundException>(() => resources.CreateAsync(
                new CreateResourceRequest { Title = "R", Type = ResourceType.Worksheet, BatchId = Guid.NewGuid() },
                "some/stored/path.txt", "text/plain", 100));
            Assert.Contains("Batch", ex.Message);
            Assert.Empty(await _db.Context.Resources.ToListAsync());
        }

        [Fact]
        public async Task UploadResource_WithARealCourseAndBatch_StillSucceeds()
        {
            // The guard above must not reject a genuinely valid selection.
            var (batch, course, _) = await SeedBatchWithSessionAsync(totalSessions: 1, includeSession: false);
            var resources = CreateResourceService();
            var dto = await resources.CreateAsync(
                new CreateResourceRequest { Title = "Worksheet", Type = ResourceType.Worksheet, CourseId = course.Id, BatchId = batch.Id },
                "some/stored/path.txt", "text/plain", 100);
            Assert.Equal(course.Id, dto.CourseId);
            Assert.Equal(batch.Id, dto.BatchId);
        }

        public void Dispose() => _db.Dispose();
    }
}
