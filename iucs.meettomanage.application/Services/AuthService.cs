using iucs.meettomanage.application.Common;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Dto.Auth;
using iucs.meettomanage.application.Helper;
using iucs.meettomanage.application.Mappings;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace iucs.meettomanage.application.Services
{
    public class AuthService : IAuthService
    {
        // How long a self-service PIN reset link stays valid after it's emailed.
        private const int ResetTokenExpiryMinutes = 30;

        // Per-account lockout, on top of the login endpoint's per-IP rate limit (Program.cs) —
        // that one alone doesn't stop an attacker who knows one target's email and spreads
        // attempts across several source IPs from brute-forcing a 4-digit PIN's 10,000-value
        // keyspace against that one account.
        private const int MaxFailedLoginAttempts = 5;
        private const int LockoutMinutes = 15;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITokenService _tokenService;
        private readonly IAuditLogService _auditLog;
        private readonly INotificationService _notificationService;
        private readonly IConfiguration _configuration;
        private readonly IPinVault? _vault;

        private const int TwoFactorChallengeMinutes = 5;
        private const int RecoveryCodeCount = 8;

        public AuthService(
            IUnitOfWork unitOfWork,
            IPasswordHasher passwordHasher,
            ITokenService tokenService,
            IAuditLogService auditLog,
            INotificationService notificationService,
            IConfiguration configuration,
            IPinVault? vault = null)
        {
            _vault = vault;
            _unitOfWork = unitOfWork;
            _passwordHasher = passwordHasher;
            _tokenService = tokenService;
            _auditLog = auditLog;
            _notificationService = notificationService;
            _configuration = configuration;
        }

        public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            var user = await FindLoginUserAsync(request.Email, cancellationToken);

            if (user is not null && user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > DateTime.UtcNow)
            {
                var minutesLeft = Math.Max(1, (int)Math.Ceiling((lockoutEnd - DateTime.UtcNow).TotalMinutes));
                throw new UnauthorizedException($"Too many failed attempts. Try again in {minutesLeft} minute(s).");
            }

            if (user is null || !_passwordHasher.Verify(request.Pin, user.PinHash))
            {
                if (user is not null)
                {
                    user.FailedLoginAttempts++;
                    if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
                    {
                        user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }

                throw new UnauthorizedException("Invalid email/mobile number or PIN.");
            }

            if (user.Status == UserStatus.Inactive)
            {
                throw new UnauthorizedException("This account has been deactivated. Contact the administrator.");
            }

            if (user.TwoFactorEnabledAtUtc is not null && user.TwoFactorSecretProtected is not null)
            {
                // PIN was right; hold the session back until an authenticator code arrives. Failed codes keep
                // counting toward the same lockout as failed PINs.
                var challenge = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                user.TwoFactorChallengeHash = Sha256(challenge);
                user.TwoFactorChallengeExpiresAtUtc = DateTime.UtcNow.AddMinutes(TwoFactorChallengeMinutes);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return new LoginResponse
                {
                    TenantId = TenantExecution.Current?.Id,
                    TenantCode = TenantExecution.Current?.Code,
                    AccessToken = string.Empty,
                    DefaultRoute = string.Empty,
                    RequiresTwoFactor = true,
                    TwoFactorChallenge = challenge,
                };
            }

            return await IssueLoginAsync(user, cancellationToken);
        }

        public async Task<LoginResponse> CompleteTwoFactorLoginAsync(CompleteTwoFactorLoginRequest request, CancellationToken cancellationToken = default)
        {
            var hash = Sha256(request.Challenge.Trim());
            var user = await _unitOfWork.Repository<User>().TrackedQuery()
                .Include(u => u.TeacherProfile)
                .FirstOrDefaultAsync(u => u.TwoFactorChallengeHash == hash, cancellationToken);
            if (user is null || user.TwoFactorChallengeExpiresAtUtc is null || user.TwoFactorChallengeExpiresAtUtc < DateTime.UtcNow)
                throw new UnauthorizedException("This sign-in has expired. Enter your PIN again.");
            if (user.LockoutEndUtc is { } lockoutEnd && lockoutEnd > DateTime.UtcNow)
                throw new UnauthorizedException("Too many failed attempts. Try again later.");
            if (user.Status == UserStatus.Inactive)
                throw new UnauthorizedException("This account has been deactivated. Contact the administrator.");

            if (!AcceptSecondFactor(user, request.Code))
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
                {
                    user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                    user.TwoFactorChallengeHash = null;
                    user.TwoFactorChallengeExpiresAtUtc = null;
                }
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                throw new UnauthorizedException("That code didn't match. Check your authenticator app and try again.");
            }

            user.TwoFactorChallengeHash = null;
            user.TwoFactorChallengeExpiresAtUtc = null;
            return await IssueLoginAsync(user, cancellationToken);
        }

        public async Task<TwoFactorStatusDto> GetTwoFactorStatusAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await _unitOfWork.Repository<User>().GetByIdAsync(userId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), userId);
            return new TwoFactorStatusDto
            {
                Available = user.Role is not (UserRole.Parent or UserRole.Marketing),
                Enabled = user.TwoFactorEnabledAtUtc is not null,
                EnabledAtUtc = user.TwoFactorEnabledAtUtc,
                RecoveryCodesLeft = RecoveryHashes(user).Count,
            };
        }

        public async Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await TwoFactorUserAsync(userId, cancellationToken);
            if (user.TwoFactorEnabledAtUtc is not null)
                throw new ConflictException("Two-step sign-in is already on. Turn it off first to move it to a new device.");

            var secret = Totp.ToBase32(Totp.NewSecret());
            user.TwoFactorSecretProtected = Vault.Protect(secret);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            var issuer = _configuration["TwoFactor:Issuer"] is { Length: > 0 } configured ? configured : "Meet to Manage";
            return new TwoFactorSetupDto { Secret = secret, OtpAuthUri = Totp.OtpAuthUri(issuer, user.Email, secret) };
        }

        public async Task<TwoFactorRecoveryCodesDto> EnableTwoFactorAsync(Guid userId, string code, CancellationToken cancellationToken = default)
        {
            var user = await TwoFactorUserAsync(userId, cancellationToken);
            if (user.TwoFactorEnabledAtUtc is not null) throw new ConflictException("Two-step sign-in is already on.");
            if (user.TwoFactorSecretProtected is null) throw new DomainValidationException("Start setup first.");
            if (!AcceptAuthenticatorCode(user, code))
                throw new DomainValidationException("That code didn't match. Make sure the time on your phone is correct and try again.");

            var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
            user.TwoFactorRecoveryCodeHashes = System.Text.Json.JsonSerializer.Serialize(codes.Select(c => Sha256(NormalizeRecovery(c))).ToList());
            user.TwoFactorEnabledAtUtc = DateTime.UtcNow;
            await _auditLog.StageAsync(AuditAction.Update, nameof(User), user.Id.ToString(), "{\"twoFactor\":\"enabled\"}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return new TwoFactorRecoveryCodesDto { RecoveryCodes = codes };
        }

        public async Task DisableTwoFactorAsync(Guid userId, string code, CancellationToken cancellationToken = default)
        {
            var user = await TwoFactorUserAsync(userId, cancellationToken);
            if (user.TwoFactorEnabledAtUtc is null) return;
            if (!AcceptSecondFactor(user, code))
                throw new DomainValidationException("That code didn't match. Enter a current code or one of your recovery codes.");
            ClearTwoFactor(user);
            await _auditLog.StageAsync(AuditAction.Update, nameof(User), user.Id.ToString(), "{\"twoFactor\":\"disabled\"}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>Admin recovery for a locked-out staff member (lost phone and recovery codes).</summary>
        public async Task ResetTwoFactorAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await _unitOfWork.Repository<User>().TrackedQuery().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), userId);
            if (user.Id == actorUserId) throw new DomainValidationException("Use your own security settings to change two-step sign-in.");
            ClearTwoFactor(user);
            await _auditLog.StageAsync(AuditAction.Update, nameof(User), user.Id.ToString(), "{\"twoFactor\":\"reset-by-admin\"}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        private async Task<LoginResponse> IssueLoginAsync(User user, CancellationToken cancellationToken)
        {
            // Suspended users (fee default) may still log in: the portal shows the
            // pending-fee "Pay Now" popup and restricts content, per the requirement.
            var permissions = await LoadPermissionClaimsAsync(user, cancellationToken);
            var token = _tokenService.CreateToken(user, permissions);

            user.LastLoginAtUtc = DateTime.UtcNow;
            user.FailedLoginAttempts = 0;
            user.LockoutEndUtc = null;
            await _auditLog.StageAsync(AuditAction.Login, nameof(User), user.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var defaultRoute = await ResolveDefaultRouteAsync(user, cancellationToken);
            return BuildResponse(user, permissions, token, defaultRoute);
        }

        public async Task<LoginResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await _unitOfWork.Repository<User>().Query()
                .Include(u => u.TeacherProfile)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), userId);

            var permissions = await LoadPermissionClaimsAsync(user, cancellationToken);
            var defaultRoute = await ResolveDefaultRouteAsync(user, cancellationToken);

            // Re-mints a fresh token carrying these just-recomputed permission claims — the
            // original login token's claims are frozen at issue time, so without this, a
            // Relationship Manager whose access an Admin just approved would keep hitting 403s
            // (and a stale sidebar) on every permission-gated call until they logged out and
            // back in. The frontend swaps its stored token for this one on every call here
            // (SessionProvider already polls this endpoint on load and periodically).
            var token = _tokenService.CreateToken(user, permissions);
            return BuildResponse(user, permissions, token, defaultRoute);
        }

        public async Task<LoginResponse> StartViewAsParentAsync(Guid actorUserId, Guid parentUserId, CancellationToken cancellationToken = default)
        {
            var parent = await _unitOfWork.Repository<User>().Query()
                .FirstOrDefaultAsync(u => u.Id == parentUserId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), parentUserId);

            // Parents only: a staff, teacher or admin account is never viewable this way.
            if (parent.Role != UserRole.Parent)
            {
                throw new DomainValidationException("Only parent accounts can be viewed this way.");
            }
            // Same rule as login: a deactivated parent couldn't sign in, so there's nothing to see.
            if (parent.Status == UserStatus.Inactive)
            {
                throw new DomainValidationException("This parent's account is deactivated, so there's no portal to view.");
            }

            var permissions = await LoadPermissionClaimsAsync(parent, cancellationToken);
            var expiresAtUtc = DateTime.UtcNow.AddMinutes(ViewAsParent.SessionMinutes);
            var token = _tokenService.CreateViewAsToken(parent, permissions, actorUserId, expiresAtUtc);

            // Recorded under the staff member's own (normal) login, naming the parent viewed.
            await _auditLog.StageAsync(AuditAction.Access, nameof(User), parent.Id.ToString(),
                $"{{\"note\":\"Started read-only 'View as parent' session ({ViewAsParent.SessionMinutes} min)\"}}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return BuildResponse(parent, permissions, token, "/parent");
        }

        public async Task<LoginResponse> GetViewAsCurrentUserAsync(Guid parentUserId, Guid actorUserId, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            var parent = await _unitOfWork.Repository<User>().Query()
                .FirstOrDefaultAsync(u => u.Id == parentUserId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), parentUserId);

            // Unlike GetCurrentUserAsync, never hand back a normal (8-hour, writable) parent token
            // here: re-mint the same read-only view-as token with its ORIGINAL expiry, so the
            // portal's periodic refresh can't stretch a 30-minute session.
            var permissions = await LoadPermissionClaimsAsync(parent, cancellationToken);
            var token = _tokenService.CreateViewAsToken(parent, permissions, actorUserId, expiresAtUtc);
            return BuildResponse(parent, permissions, token, "/parent");
        }

        public async Task EndViewAsParentAsync(Guid parentUserId, CancellationToken cancellationToken = default)
        {
            // Called with the view-as token itself, so AuditLogService credits the staff member.
            await _auditLog.StageAsync(AuditAction.Access, nameof(User), parentUserId.ToString(),
                "{\"note\":\"Ended 'View as parent' session\"}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<CurrentAccessSnapshot?> GetCurrentAccessAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var user = await _unitOfWork.Repository<User>().Query()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return null;
            }

            return new CurrentAccessSnapshot
            {
                Role = user.Role,
                Status = user.Status,
                Permissions = await LoadPermissionClaimsAsync(user, cancellationToken),
            };
        }

        /// <summary>
        /// The login box takes an email OR a parent's mobile number — parents with no email are
        /// set up to sign in with their phone (ParentLogin). Phone login is for Parent accounts
        /// only; a number shared by more than one parent account is ambiguous, so it returns
        /// null (the caller's generic "invalid" error) rather than guessing which account.
        /// Tracked, because a failed attempt updates the lockout counters on it.
        /// </summary>
        private async Task<User?> FindLoginUserAsync(string identifier, CancellationToken cancellationToken)
        {
            var value = identifier.Trim();
            if (!ParentLogin.LooksLikePhone(value))
            {
                var email = value.ToLowerInvariant();
                return await _unitOfWork.Repository<User>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            }

            var phone = ParentLogin.NormalizePhone(value)!;
            var lastFour = phone[^4..];
            var tenantMobileLogin = bool.TryParse(_configuration["MultiTenancy:Enabled"], out var enabled) && enabled;
            var candidates = (await _unitOfWork.Repository<User>().TrackedQuery()
                    .Where(u => (tenantMobileLogin || u.Role == UserRole.Parent) && u.Phone != null && u.Phone.Contains(lastFour))
                    .ToListAsync(cancellationToken))
                .Where(u => ParentLogin.NormalizePhone(u.Phone) == phone)
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        /// <summary>
        /// Alerts everyone able to reset a PIN (Admins, and Sub Admins with User Management edit)
        /// in their notification feed — the forgot-PIN path for parents with no email.
        /// </summary>
        private async Task NotifyPinResetStaffAsync(User parent, CancellationToken cancellationToken)
        {
            var module = PermissionModule.UserManagement.ToString();
            var editors = _unitOfWork.Repository<SubAdminPermission>().Query()
                .Where(p => p.Module == module && p.CanEdit)
                .Select(p => p.UserId);
            var staff = await _unitOfWork.Repository<User>().Query()
                .Where(u => u.Status == UserStatus.Active
                    && (u.Role == UserRole.Admin || (u.Role == UserRole.SubAdmin && editors.Contains(u.Id))))
                .ToListAsync(cancellationToken);

            var name = $"{parent.FirstName} {parent.LastName}".Trim();
            var subject = $"PIN reset requested: {name} ({parent.Phone})";
            var body =
                $"<p>{System.Net.WebUtility.HtmlEncode(name)} (mobile {System.Net.WebUtility.HtmlEncode(parent.Phone ?? "")}) " +
                "tapped <b>Forgot PIN</b> but has no email to receive a reset link.</p>" +
                "<p>Open <b>Users</b>, find this parent, use <b>Reset PIN</b> and send them the new PIN on WhatsApp.</p>";
            foreach (var member in staff)
            {
                await _notificationService.SendEmailAsync(
                    member.Id, member.Email, NotificationType.General, subject, body, cancellationToken: cancellationToken);
            }
        }

        public async Task RequestPinResetAsync(ForgotPinRequest request, CancellationToken cancellationToken = default)
        {
            // Same identifier as the login box: email, or a parent's mobile number.
            var user = await FindLoginUserAsync(request.Email, cancellationToken);

            // Product decision: reveal whether the identifier has an account (trades away
            // anti-enumeration protection for a clearer "did my request even do anything?"
            // signal, since silent success was being mistaken for a delivery bug).
            if (user is null || user.Status == UserStatus.Inactive)
            {
                throw new NotFoundException("We couldn't find an account for that email or mobile number.");
            }

            // A WhatsApp-only parent has no email to receive a reset link (and SMS/WhatsApp
            // sending may be off), so ask the staff who can reset PINs to do it and send the new
            // one on WhatsApp. The response is identical either way (no account discovery).
            if (!ParentLogin.IsDeliverable(user.Email))
            {
                await NotifyPinResetStaffAsync(user, cancellationToken);
                return;
            }

            var token = SecureTokenGenerator.Generate();
            await _unitOfWork.Repository<PinResetToken>().AddAsync(
                new PinResetToken
                {
                    UserId = user.Id,
                    Token = token,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(ResetTokenExpiryMinutes),
                },
                cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var frontendBaseUrl = iucs.meettomanage.application.Common.TenantExecution.Url(_configuration["Frontend:BaseUrl"] ?? "http://localhost:5173");
            var resetUrl = $"{frontendBaseUrl}/reset-pin?token={Uri.EscapeDataString(token)}";

            await _notificationService.SendTemplatedEmailAsync(
                user.Id,
                user.Email,
                NotificationType.General,
                "pin-reset",
                new Dictionary<string, string>
                {
                    ["FirstName"] = user.FirstName,
                    ["Email"] = user.Email,
                    ["ResetUrl"] = resetUrl,
                    ["ExpiryMinutes"] = ResetTokenExpiryMinutes.ToString(),
                },
                cancellationToken);
        }

        public async Task ChangePinAsync(Guid userId, ChangePinRequest request, CancellationToken cancellationToken = default)
        {
            // Client: the Admin account had no way at all to change its own PIN (Users -> Reset PIN
            // deliberately excludes Admins). Any signed-in user can now, from their account settings.
            var user = await _unitOfWork.Repository<User>().GetByIdAsync(userId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), userId);

            if (!_passwordHasher.Verify(request.CurrentPin, user.PinHash))
            {
                throw new DomainValidationException("Your current PIN is not correct.");
            }
            if (request.NewPin == request.CurrentPin)
            {
                throw new DomainValidationException("Choose a new PIN that's different from your current one.");
            }

            user.PinHash = _passwordHasher.Hash(request.NewPin);
            // A PIN the user chose themselves is never kept in readable form.
            user.PinEncrypted = null;
            await _auditLog.StageAsync(AuditAction.Update, nameof(User), user.Id.ToString(),
                "{\"note\":\"User changed their own PIN\"}", cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task ResetPinAsync(ResetPinRequest request, CancellationToken cancellationToken = default)
        {
            var resetToken = await _unitOfWork.Repository<PinResetToken>()
                .FirstOrDefaultAsync(t => t.Token == request.Token, cancellationToken);

            if (resetToken is null || resetToken.UsedAtUtc is not null || resetToken.ExpiresAtUtc < DateTime.UtcNow)
            {
                throw new DomainValidationException("This reset link is invalid or has expired. Request a new one.");
            }

            var user = await _unitOfWork.Repository<User>().GetByIdAsync(resetToken.UserId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), resetToken.UserId);

            user.PinHash = _passwordHasher.Hash(request.NewPin);
            // A PIN the user chose themselves is never kept in readable form; drop the older system-issued one.
            user.PinEncrypted = null;
            resetToken.UsedAtUtc = DateTime.UtcNow;

            await _auditLog.StageAsync(AuditAction.Update, nameof(User), user.Id.ToString(), cancellationToken: cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// The frontend route a user lands on right after login: their assigned
        /// role's configured route for Sub Admins, else the fixed portal home
        /// for their account type.
        /// </summary>
        private async Task<string> ResolveDefaultRouteAsync(User user, CancellationToken cancellationToken)
        {
            if (user.Role == UserRole.SubAdmin && user.RoleDefinitionId.HasValue)
            {
                var role = await _unitOfWork.Repository<RoleDefinition>()
                    .GetByIdAsync(user.RoleDefinitionId.Value, cancellationToken);
                if (!string.IsNullOrWhiteSpace(role?.DefaultRoute))
                {
                    return role.DefaultRoute;
                }
            }

            return user.Role switch
            {
                UserRole.Admin => "/admin",
                UserRole.Teacher => "/teacher",
                UserRole.Parent => "/parent",
                UserRole.AdmissionTeam => "/admission",
                UserRole.SubAdmin => "/subadmin",
                UserRole.Marketing => "/marketing",
                _ => "/login",
            };
        }

        /// <summary>System RoleDefinition name backing each fixed-portal UserRole's default grant.</summary>
        private static string? SystemRoleNameFor(UserRole role) => role switch
        {
            UserRole.Teacher => "teacher",
            UserRole.Parent => "parent",
            UserRole.AdmissionTeam => "admission",
            _ => null,
        };

        /// <summary>
        /// Sub Admins carry their own per-user grant (SubAdminPermission — possibly seeded
        /// from a preset but editable per person from then on). Every other non-Admin role
        /// (Teacher/Parent/Admission Team) shares one grant: its matching system
        /// RoleDefinition, editable from the same Roles &amp; Permissions → Role Presets screen
        /// as every other role — there is no separate hardcoded baseline to keep in sync.
        /// Admin needs no claims; the authorization handler grants it every permission by role.
        /// </summary>
        private async Task<IReadOnlyList<string>> LoadPermissionClaimsAsync(User user, CancellationToken cancellationToken)
        {
            if (user.Role == UserRole.SubAdmin)
            {
                var grants = await _unitOfWork.Repository<SubAdminPermission>().Query()
                    .Where(p => p.UserId == user.Id)
                    .ToListAsync(cancellationToken);
                return ToClaims(grants.Select(g => (g.Module, g.CanView, g.CanCreate, g.CanEdit, g.CanDelete, g.CanApprove)));
            }

            var roleName = SystemRoleNameFor(user.Role);
            if (roleName is null)
            {
                return [];
            }

            var role = await _unitOfWork.Repository<RoleDefinition>().Query()
                .Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);

            return role is null
                ? []
                : ToClaims(role.Permissions.Select(p => (p.Module, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.CanApprove)));
        }

        private static List<string> ToClaims(
            IEnumerable<(string Module, bool CanView, bool CanCreate, bool CanEdit, bool CanDelete, bool CanApprove)> grants)
        {
            var claims = new List<string>();
            foreach (var grant in grants)
            {
                if (grant.CanView) claims.Add($"{grant.Module}:{PermissionAction.View}");
                if (grant.CanCreate) claims.Add($"{grant.Module}:{PermissionAction.Create}");
                if (grant.CanEdit) claims.Add($"{grant.Module}:{PermissionAction.Edit}");
                if (grant.CanDelete) claims.Add($"{grant.Module}:{PermissionAction.Delete}");
                if (grant.CanApprove) claims.Add($"{grant.Module}:{PermissionAction.Approve}");
            }

            return claims;
        }

        private IPinVault Vault => _vault ?? throw new InvalidOperationException("Two-step sign-in needs the PIN vault to be configured.");

        private async Task<User> TwoFactorUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            var user = await _unitOfWork.Repository<User>().TrackedQuery().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                ?? throw new NotFoundException(nameof(User), userId);
            if (user.Role == UserRole.Parent)
                throw new ForbiddenException("Two-step sign-in is for staff accounts. Family accounts can use the app's fingerprint or face lock.");
            if (user.Role == UserRole.Marketing)
                throw new ForbiddenException("Two-step sign-in isn't available for the marketing console yet.");
            return user;
        }

        private bool AcceptSecondFactor(User user, string? code) => AcceptAuthenticatorCode(user, code) || AcceptRecoveryCode(user, code);

        private bool AcceptAuthenticatorCode(User user, string? code)
        {
            if (user.TwoFactorSecretProtected is null || Vault.TryReveal(user.TwoFactorSecretProtected) is not { } secret) return false;
            var step = Totp.Verify(Totp.FromBase32(secret), code, DateTime.UtcNow, user.TwoFactorLastUsedStep);
            if (step is null) return false;
            user.TwoFactorLastUsedStep = step;
            return true;
        }

        private static bool AcceptRecoveryCode(User user, string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var hashes = RecoveryHashes(user);
            if (!hashes.Remove(Sha256(NormalizeRecovery(code)))) return false;
            user.TwoFactorRecoveryCodeHashes = System.Text.Json.JsonSerializer.Serialize(hashes);
            return true;
        }

        private static List<string> RecoveryHashes(User user) =>
            string.IsNullOrEmpty(user.TwoFactorRecoveryCodeHashes) ? []
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(user.TwoFactorRecoveryCodeHashes) ?? [];

        private static void ClearTwoFactor(User user)
        {
            user.TwoFactorEnabledAtUtc = null;
            user.TwoFactorSecretProtected = null;
            user.TwoFactorRecoveryCodeHashes = null;
            user.TwoFactorLastUsedStep = null;
            user.TwoFactorChallengeHash = null;
            user.TwoFactorChallengeExpiresAtUtc = null;
        }

        /// <summary>"7KQ2-M9XD" style: 8 characters from an alphabet without look-alikes.</summary>
        private static string NewRecoveryCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
            var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
            return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        }

        private static string NormalizeRecovery(string code) => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private static string Sha256(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static LoginResponse BuildResponse(User user, IReadOnlyList<string> permissions, TokenResult? token, string defaultRoute)
        {
            return new LoginResponse
            {
                TenantId = TenantExecution.Current?.Id,
                TenantCode = TenantExecution.Current?.Code,
                AccessToken = token?.AccessToken ?? string.Empty,
                ExpiresAtUtc = token?.ExpiresAtUtc ?? default,
                User = user.ToDto(),
                Permissions = permissions,
                DefaultRoute = defaultRoute,
            };
        }
    }
}
