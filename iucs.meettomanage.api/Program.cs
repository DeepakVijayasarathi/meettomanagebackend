using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Data;
using iucs.meettomanage.api.Middleware;
using iucs.meettomanage.api.Services;
using iucs.meettomanage.application;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Options;
using iucs.meettomanage.application.Services;
using iucs.meettomanage.domain.Common;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Data.Interceptors;
using iucs.meettomanage.domain.Entities.Users;
using iucs.meettomanage.domain.Enums;
using iucs.meettomanage.domain.Repository;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Microsoft.AspNetCore.SignalR;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;

if (args.Contains("platform-password-hash", StringComparer.Ordinal))
{
    PlatformPasswordCommand.Run();
    return;
}
var builder = WebApplication.CreateBuilder(args);
var tenantMode = builder.Configuration.GetValue<bool>("MultiTenancy:Enabled");
// Local development may reuse its existing PostgreSQL server credentials while
// keeping the control plane in a new database. Never infer production mappings.
if (tenantMode && builder.Environment.IsDevelopment())
{
    var localConnection = builder.Configuration.GetConnectionString("ReaderNestDb");
    if (!string.IsNullOrWhiteSpace(localConnection))
    {
        var local = new Npgsql.NpgsqlConnectionStringBuilder(localConnection);
        if (local.Host is "localhost" or "127.0.0.1" or "::1")
        {
            if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("MasterDb")))
                builder.Configuration["ConnectionStrings:MasterDb"] = new Npgsql.NpgsqlConnectionStringBuilder(local.ConnectionString)
                    { Database = "meettomanage_tenants" }.ConnectionString;
            if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("TenantServer")))
                builder.Configuration["ConnectionStrings:TenantServer"] = new Npgsql.NpgsqlConnectionStringBuilder(local.ConnectionString)
                    { Database = "postgres" }.ConnectionString;
        }
    }
}
if (tenantMode)
{
    builder.Services.AddTenantServices(builder.Configuration);
    // SaaS layer: plans, subscriptions, entitlements, platform billing and reminders (master database).
    builder.Services.AddSaasServices(builder.Configuration);
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        // Enums travel as their names ("Teacher", "Overdue"), matching how they are stored
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// Cross-cutting platform services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<AuditableEntityInterceptor>();

// Persistence
var connectionString =
    builder.Configuration.GetConnectionString("ReaderNestDb") ??
    Environment.GetEnvironmentVariable("ConnectionStrings__ReaderNestDb");

if (!tenantMode && string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Database connection string is missing.");
}

builder.Services.AddDbContext<MeetToManageDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(tenantMode
        ? TenantServices.BoundTenant(serviceProvider).RequireConnectionString()
        : connectionString).AddInterceptors(serviceProvider.GetRequiredService<AuditableEntityInterceptor>());
    if (tenantMode)
    {
        // Plan limits (students/staff) are enforced centrally on save. This must run BEFORE the login
        // directory interceptor: that one reserves the user's e-mail/phone in the master registry, and a
        // save rejected for being over the plan limit must not leave an orphan reservation behind.
        options.AddInterceptors(serviceProvider.GetRequiredService<iucs.meettomanage.api.Saas.EntitlementInterceptor>());
        options.AddInterceptors(serviceProvider.GetRequiredService<LoginDirectoryInterceptor>());
    }
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Application services + API-layer implementations of its abstractions
builder.Services.AddApplication();
builder.Services.AddScoped<ITokenService, JwtTokenService>();
// Real SMTP delivery driven by the DB "email" integration config (Settings →
// Integrations); logs and no-ops safely when that integration is off/unconfigured.
builder.Services.AddScoped<SmtpEmailSender>();
builder.Services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<SmtpEmailSender>());
// WhatsApp Business Cloud API delivery, driven by the DB "whatsapp" integration.
builder.Services.AddScoped<IWhatsAppSender, WhatsAppSender>();
// SMS delivery (MSG91/Twilio), driven by the DB "sms" integration.
builder.Services.AddScoped<ISmsSender, SmsSender>();
// Marketing-site chat assistant (Gemini). Key from Gemini:ApiKey — user-secrets locally,
// Gemini__ApiKey env var in prod, never committed.
builder.Services.AddHttpClient(nameof(GeminiSalesAssistant), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<ISalesAssistant, GeminiSalesAssistant>();
builder.Services.AddHttpClient(nameof(OllamaChatClient), client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<IAiChatClient, OllamaChatClient>();
// S3-compatible object storage (Hetzner Object Storage) everywhere — dev and prod both, so
// an upload made locally behaves identically to one made against the real deployment instead
// of silently depending on which environment you're in. Configured via Storage:S3:* (real
// credentials come from user-secrets locally, environment variables in prod — never committed).
//
// Each client picks S3 or a folder on this server's disk (Storage:LocalPath -- must be a mounted
// host folder, see LocalFileStorage) on Admin → Settings → File storage; the choice is kept in the
// database and applies live (StorageProviderSwitch), with Storage:Provider as the starting value.
// Both stores stay wired up so files are found wherever they are (RoutingFileStorage); existing
// files are copied between them with that page's Sync buttons, or `storage-migrate` (see StorageMigration).
builder.Services.AddSingleton<StorageSyncService>();
builder.Services.AddSingleton<StorageProviderSwitch>();
var s3Storage = S3FileStorage.IsConfigured(builder.Configuration) ? new S3FileStorage(builder.Configuration) : null;
if (s3Storage is not null)
{
    if (tenantMode) builder.Services.AddScoped(sp => TenantServices.InTenant(sp, () => new S3FileStorage(builder.Configuration, s3Storage)));
    else builder.Services.AddSingleton(s3Storage);
}

// With S3 configured it doubles as the fallback for files not yet copied to this server.
ServiceLifetime fileLifetime = tenantMode ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;
builder.Services.Add(new ServiceDescriptor(typeof(LocalFileStorage), sp => tenantMode
    ? TenantServices.InTenant(sp, () => new LocalFileStorage(sp.GetRequiredService<IWebHostEnvironment>(), builder.Configuration,
        sp.GetRequiredService<IHttpContextAccessor>(), sp.GetService<S3FileStorage>()))
    : new LocalFileStorage(
    sp.GetRequiredService<IWebHostEnvironment>(),
    builder.Configuration,
    sp.GetRequiredService<IHttpContextAccessor>(),
    s3Storage), fileLifetime));
builder.Services.Add(new ServiceDescriptor(typeof(RoutingFileStorage), sp => new RoutingFileStorage(
    sp.GetRequiredService<StorageProviderSwitch>(), sp.GetRequiredService<LocalFileStorage>(), sp.GetService<S3FileStorage>()), fileLifetime));
builder.Services.Add(new ServiceDescriptor(typeof(IFileStorage), sp => sp.GetRequiredService<RoutingFileStorage>(), fileLifetime));
builder.Services.Add(new ServiceDescriptor(typeof(IDirectUploadStorage), sp => sp.GetRequiredService<RoutingFileStorage>(), fileLifetime));
// Parses uploaded bulk-import spreadsheets (.csv/.xlsx) for Users/Students/Departments/
// Courses/Package Plans/Quiz Questions — stateless, so singleton is fine.
builder.Services.AddSingleton<IBulkFileReader, BulkFileReader>();
// Signs room-scoped Jitsi join tokens from the DB "jitsi" integration's appId/appSecret;
// no-ops (null token, unsigned join) until an admin sets them — see JITSI_ARCHITECTURE.md.
builder.Services.AddSingleton<IJitsiTokenService, JitsiTokenService>();
// Renders the "Bill of Supply" invoice PDF (QuestPDF) — stateless beyond a static embedded
// logo loaded once, so singleton is fine.
builder.Services.AddSingleton<IInvoicePdfGenerator, InvoicePdfGenerator>();
// Server Monitoring dashboard: polls each server's rn-status agent (Monitoring:Servers config).
// Short timeout so one down/slow server can't stall the whole summary request.
builder.Services.AddHttpClient("Prometheus", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<IPrometheusClient, PrometheusClient>();
// Dual-gateway abstraction: the dispatcher routes to Razorpay/Cashfree using live
// credentials from Settings → Integrations, and falls back to the simulated gateway
// while an integration is disabled or its keys are blank.
builder.Services.AddSingleton<SimulatedPaymentGateway>();
builder.Services.AddScoped<iucs.meettomanage.api.Services.Payments.IGatewayAdapter, iucs.meettomanage.api.Services.Payments.RazorpayGateway>();
builder.Services.AddScoped<iucs.meettomanage.api.Services.Payments.IGatewayAdapter, iucs.meettomanage.api.Services.Payments.CashfreeGateway>();
builder.Services.AddScoped<IPaymentGateway, iucs.meettomanage.api.Services.Payments.PaymentGatewayDispatcher>();
// Auto billing: recurring invoice generation + overdue flagging + fee suspension
builder.Services.AddTenantWorker<BillingBackgroundService>(tenantMode);
// Session reminders, delayed-session alerts
builder.Services.AddTenantWorker<SessionReminderBackgroundService>(tenantMode);
builder.Services.AddTenantWorker<AbandonedClassCompletionBackgroundService>(tenantMode);
// Automatic no-show detection: flags a session once its grace period elapses with one
// side never having joined, instead of relying solely on a human clicking "Mark No-Show"
builder.Services.AddTenantWorker<NoShowDetectionBackgroundService>(tenantMode);
// Batch payment plan reminders: "payment after N sessions" / "payment due on a specific date"
builder.Services.AddTenantWorker<PaymentPlanReminderBackgroundService>(tenantMode);
// Catches a completed class that never received a recording — auto-record can start
// with no error yet still fail later in the pipeline, which nothing else catches
builder.Services.AddTenantWorker<RecordingReconciliationBackgroundService>(tenantMode);
// CRM integration: lead webhooks, no-op until Integrations:CrmWebhookUrl is set
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ICrmNotifier, WebhookCrmNotifier>();
// Automated reports: weekly KPI digest to admins
builder.Services.AddTenantWorker<ReportsDigestBackgroundService>(tenantMode);
builder.Services.AddTenantWorker<BulkEmailQueueBackgroundService>(tenantMode);
// Progress reports: seeds an empty monthly draft per active child on the 1st
builder.Services.AddTenantWorker<ProgressReportsBackgroundService>(tenantMode);
builder.Services.AddTenantWorker<LeaveCancellationReportBackgroundService>(tenantMode);

// Authentication: JWT bearer
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.SectionName));
builder.Services.Configure<iucs.meettomanage.application.Helper.PinVaultOptions>(o =>
{
    o.Key = builder.Configuration["PinVault:Key"];
    o.FallbackSecret = builder.Configuration["Jwt:SigningKey"];
});
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    // Fail at startup with an actionable message instead of letting every request
    // die in the JWT handler with IDX10703 (zero-length key). HS256 needs >= 256 bits.
    throw new InvalidOperationException(
        "Jwt:SigningKey is missing or shorter than 32 bytes. " +
        "Set the Jwt__SigningKey environment variable (or appsettings) to a random secret of at least 32 characters.");
}
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // SignalR websockets can't send an Authorization header: the classroom hub
        // authenticates via the standard access_token query parameter instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.Value is "/api/auth/login" or "/api/auth/forgot-pin"
                    || (context.HttpContext.Items["TenantPathCode"] is string
                        && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null))
                {
                    context.NoResult();
                    return Task.CompletedTask;
                }
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            // The JWT itself stays valid for up to AccessTokenMinutes (8h) regardless of what
            // happens to the account after it was issued. Re-resolving access here — not just
            // status — closes that window down to the request itself: a revoked permission, a
            // changed role, or a role preset's own permissions being edited now takes effect on
            // the very next request from anyone holding a token for that account, instead of
            // silently staying valid until the token expires or they happen to log in again.
            // Costs one indexed PK lookup (plus the account's permission rows) per authenticated
            // call — the same shape of cost this check already paid for the status-only version.
            OnTokenValidated = async context =>
            {
                if (context.Principal?.FindFirstValue("purpose") == "platform-admin"
                    && context.Principal.IsInRole("PlatformAdmin"))
                {
                    if (!tenantMode) context.Fail("Platform administration is not enabled.");
                    return;
                }
                if (tenantMode)
                {
                    if (!Guid.TryParse(context.Principal?.FindFirstValue(TenantContext.TenantIdClaim), out var tenantId)
                        || context.Principal?.FindFirstValue(TenantContext.TenantCodeClaim) is not { } tenantCode)
                    {
                        context.Fail("A tenant-bound token is required.");
                        return;
                    }
                    try
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<TenantResolver>()
                            .ResolveTokenAsync(tenantId, tenantCode, context.HttpContext.RequestAborted);
                    }
                    catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("TenantAuthentication").LogWarning(ex, "Tenant resolution failed.");
                        context.Fail("The tenant is unavailable.");
                        return;
                    }
                }
                // Jibri's recording-observer token (ClassroomHub-only, see
                // CreateRecordingObserverHubToken) carries a throwaway subject that never
                // resolves to a real User row -- the active-account check below would always
                // fail it. It's short-lived and scoped to one sessionId claim that
                // ClassroomHub.JoinSession itself verifies, so there's nothing further to
                // refresh here.
                if (context.Principal?.FindFirstValue("purpose") == "recording-observer")
                {
                    return;
                }

                // Same reasoning as the recording-observer bypass just above, for a Guest Link's
                // ClassroomHub token (see CreateGuestClassroomHubToken / SessionService.
                // GetGuestJoinAsync): a caretaker joining via a shared link has no real User row
                // behind them either, and ClassroomHub.JoinSession does its own sessionId-scoped
                // check for this purpose in place of the normal participant check.
                if (context.Principal?.FindFirstValue("purpose") == "guest-classroom")
                {
                    return;
                }

                var userIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(userIdClaim, out var userId))
                {
                    context.Fail("Token is missing a valid subject.");
                    return;
                }

                var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
                iucs.meettomanage.application.Dto.Auth.CurrentAccessSnapshot? access;
                try
                {
                    access = await authService.GetCurrentAccessAsync(userId, context.HttpContext.RequestAborted);
                }
                catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    if (tenantMode)
                    {
                        context.Fail("Current account access could not be verified.");
                        return;
                    }
                    // A DB hiccup here (slow query, exhausted connection pool under class-time
                    // load) used to throw out of this handler, which JwtBearer turns into a 401 —
                    // and the frontend treats any 401 as "session over": it clears the token and
                    // hard-redirects to /login, tearing down a live class mid-lesson. Reported
                    // live as "the LMS kept throwing me out" during an evening batch. The token
                    // itself already passed signature + lifetime validation, so fall back to the
                    // claims it was issued with for this one request; the next request that
                    // reaches the DB re-applies any revocation as before.
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JwtBearer")
                        .LogWarning(ex, "Live access re-check failed for user {UserId}; using token claims for this request.", userId);
                    return;
                }

                if (access is null || access.Status != UserStatus.Active)
                {
                    context.Fail("This account is no longer active.");
                    return;
                }

                // A "view as parent" token also stands for the staff member looking: if they've
                // since been deactivated or lost User Management view (or the account is no
                // longer a parent), the session ends on its very next request -- same live
                // re-check as above, applied to the person actually behind it.
                var viewAsActorClaim = context.Principal?.FindFirstValue(iucs.meettomanage.application.Common.ViewAsParent.ActorClaimType);
                if (viewAsActorClaim is not null)
                {
                    if (!Guid.TryParse(viewAsActorClaim, out var actorId) || access.Role != UserRole.Parent)
                    {
                        context.Fail("This view-as session is no longer valid.");
                        return;
                    }

                    var actor = await authService.GetCurrentAccessAsync(actorId, context.HttpContext.RequestAborted);
                    var actorAllowed = actor is not null
                        && actor.Status == UserStatus.Active
                        && (actor.Role == UserRole.Admin
                            || actor.Permissions.Contains(iucs.meettomanage.application.Common.ViewAsParent.RequiredPermission));
                    if (!actorAllowed)
                    {
                        context.Fail("You no longer have access to view this parent's account.");
                        return;
                    }
                }

                if (context.Principal!.Identity is ClaimsIdentity identity)
                {
                    // The role claim drives [Authorize(Roles = ...)] checks; Admin already gets
                    // every permission implicitly (PermissionAuthorizationHandler), so it carries
                    // none of these claims to begin with — nothing to refresh for that role.
                    foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
                    {
                        identity.RemoveClaim(claim);
                    }
                    identity.AddClaim(new Claim(ClaimTypes.Role, access.Role.ToString()));

                    foreach (var claim in identity.FindAll(JwtTokenService.PermissionClaimType).ToList())
                    {
                        identity.RemoveClaim(claim);
                    }
                    identity.AddClaims(access.Permissions.Select(p => new Claim(JwtTokenService.PermissionClaimType, p)));
                }
            },
        };
    });

// In-memory cache for read-heavy, rarely-changing lookups (email template rendering
// today; see EmailTemplateService) — process-local, fine for the current
// single-instance deployment.
builder.Services.AddMemoryCache();

// Real-time classroom layer (roster, whiteboard sync, quizzes, celebrations)
builder.Services.AddSignalR(options =>
{
    if (tenantMode) options.AddFilter<TenantHubFilter>();
});

// Live Server Monitoring push (replaces the dashboard's client-side poll)
builder.Services.AddSingleton<iucs.meettomanage.api.Hubs.MonitoringConnectionTracker>();
builder.Services.AddTenantWorker<iucs.meettomanage.api.Services.MonitoringBroadcastService>(tenantMode);

// Brute-force protection on login: framework-provided rate limiting (built into
// ASP.NET Core since .NET 7, no extra package). Rejects immediately over the limit
// rather than queuing, so a flood gets a fast 429 instead of stacking up requests.
// Partitioned per client IP so one attacker can't lock out everyone else.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
    // Anti-spam on the public store "Enroll now" form — same shape as "login" but a
    // looser limit, since this is legitimate-traffic throttling, not brute-force defense.
    // Anonymous /pay/{token} admission payment page: legitimate parents open it a handful of
    // times, so this only needs to blunt token guessing and gateway-link spam.
    options.AddPolicy("admission-payment", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
    // Public marketing chat: every message is a paid model call, so this caps one visitor's
    // spend while still allowing a real back-and-forth conversation.
    // Marketing-site analytics beacons: generous enough for a visitor browsing quickly, low
    // enough that one IP can't flood the events table.
    options.AddPolicy("marketing-track", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy("marketing-chat", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));
    options.AddPolicy("store-inquiry", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));
    // Self-service PIN reset (forgot-pin/reset-pin): anonymous and email-enumeration-adjacent
    // like "login", so it gets the same brute-force-defense shape rather than the looser
    // "store-inquiry" one.
    options.AddPolicy("pin-reset", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
    // Public demo join redirect: legitimate traffic can legitimately retry a handful of times
    // (a parent reloading, or clicking their own link plus each invitee's from one household/
    // IP), but booking ids aren't secret once a link exists, so this still caps blind GUID
    // enumeration against the endpoint -- looser than "login"/"pin-reset" since there's no
    // credential to brute-force here, just a link to guess.
    options.AddPolicy("demo-join", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));
});

// Authorization: module/action permission policies (Admin passes implicitly)
builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
    policy.RequireAuthenticatedUser().RequireRole("PlatformAdmin").RequireClaim("purpose", "platform-admin")));
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Origins must be exact scheme+host values (no trailing slash, no paths); a
// trailing slash never matches the browser's Origin header, so trim it here.
var allowedOrigins = (builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Select(origin => origin?.Trim().TrimEnd('/') ?? string.Empty)
    .Where(origin => origin.Length > 0)
    .ToArray();
// The CORS protocol forbids combining a literal wildcard origin with AllowCredentials()
// (below) — browsers reject it outright. So "*" here doesn't send a literal wildcard;
// it switches the policy to SetIsOriginAllowed(_ => true), which reflects whatever
// Origin header the caller sent back as the allowed origin on every request. That's
// the actual "any origin" mechanism once credentials are involved, and it means every
// origin can make credentialed calls to this API — only use "*" where that's intended
// (e.g. local development), never for a real production deployment.
var allowAnyOrigin = allowedOrigins.Contains("*");
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .AllowCredentials()
            // Local storage's part-upload endpoint answers each part with its ETag, which the
            // browser's upload code must be able to read (S3 exposes it via the bucket's CORS).
            .WithExposedHeaders("ETag")
            // Browsers re-sent a preflight before nearly every API call; let them reuse it.
            .SetPreflightMaxAge(TimeSpan.FromHours(2));

        if (allowAnyOrigin)
        {
            policy.SetIsOriginAllowed(_ => true);
        }
        else
        {
            policy.WithOrigins(allowedOrigins);
        }
    }));

// Reported live as "APIs take 7 seconds": session lists were several MB of JSON. Compressing
// JSON responses cuts them roughly tenfold on the wire.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

var app = builder.Build();

// `dotnet iucs.meettomanage.api.dll storage-migrate --to local|s3`: copy uploaded files between S3
// and the local folder, then exit without starting the web server (see StorageMigration).
if (StorageMigration.IsRequested(args))
{
    if (tenantMode) throw new InvalidOperationException("Use the tenant's storage-sync page in multi-tenant mode.");
    Environment.ExitCode = await StorageMigration.RunAsync(args, app.Services, app.Configuration);
    return;
}

// Must run before anything that reads Connection.RemoteIpAddress (the login rate
// limiter below): the API is served through a TLS-terminating reverse proxy in
// production, so without this every request otherwise reports the proxy's IP,
// collapsing the per-client limiter into one shared counter. KnownNetworks/
// KnownProxies are cleared because the proxy's address isn't fixed in this
// container deployment; ForwardLimit keeps only the immediate hop trusted.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
// Defaults only trust loopback; the reverse proxy's actual address isn't fixed
// in this container deployment, so trust the immediate hop regardless of address.
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);
app.UseResponseCompression();

app.UseMiddleware<ExceptionHandlingMiddleware>();

// A public client URL carries routing metadata. The endpoint's normal bearer,
// capability-token, HMAC or IP checks still authorize the requested operation.
app.Use(async (http, next) =>
{
    if (!tenantMode && http.Request.Path.StartsWithSegments("/api/platform"))
    {
        http.Response.StatusCode = 404;
        return;
    }
    string? pathCode = null;
    if (tenantMode && http.Request.Path.StartsWithSegments("/t", out var remaining))
    {
        var segments = (remaining.Value ?? "").Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1)
        {
            pathCode = segments[0];
            http.Items["TenantPathCode"] = pathCode;
            var prefix = new PathString("/t/" + segments[0]);
            http.Request.PathBase = http.Request.PathBase.Add(prefix);
            http.Request.Path = remaining.Value!.Length > segments[0].Length + 1
                ? new PathString(remaining.Value[(segments[0].Length + 1)..]) : new PathString("/");
        }
    }
    if (tenantMode)
    {
        var domainCode = await http.RequestServices.GetRequiredService<TenantDomainResolver>()
            .ResolveCodeAsync(http.Request, http.RequestAborted);
        if (domainCode is not null)
        {
            if (pathCode is not null && !string.Equals(pathCode, domainCode, StringComparison.Ordinal))
                throw new ForbiddenException("This session URL belongs to a different tenant domain.");
            http.Items["TenantPathCode"] = domainCode;
            // Registered custom origins are dynamic and cannot be represented by the
            // startup-only CORS list. Bearer tokens are still required for private data.
            if (http.Request.Headers.Origin.FirstOrDefault() is { } origin)
            {
                http.Response.Headers.AccessControlAllowOrigin = origin;
                http.Response.Headers.AccessControlAllowCredentials = "true";
                http.Response.Headers.AccessControlAllowMethods = "GET,POST,PUT,PATCH,DELETE,OPTIONS";
                http.Response.Headers.AccessControlAllowHeaders = "Authorization,Content-Type";
                http.Response.Headers.Vary = "Origin";
            }
        }
    }
    await next(http);
});
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
else
{
    // In development the SPA calls the plain-HTTP port; redirecting to HTTPS
    // breaks CORS preflight requests, so only redirect outside development.
    app.UseHttpsRedirection();
    app.UseHsts();

    // Security headers: scoped to non-development so they never fight the dev-only
    // Scalar/OpenAPI UI, which needs inline scripts/styles a strict CSP would block.
    // This is a pure JSON API in production, so the policy can be tight.
    app.Use(async (context, next) =>
    {
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
        await next();
    });
}

app.UseCors();

app.UseAuthentication();
// "View as parent" sessions are look-only: refuse their writes/joins before authorization runs.
app.UseMiddleware<ViewAsReadOnlyMiddleware>();
app.UseMiddleware<MarketingScopeMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
if (tenantMode) app.UseMiddleware<TenantRequestMiddleware>();

app.MapControllers();
app.MapGet("/api/tenant/resolve", (HttpContext http) => Results.Ok(new
{
    tenantCode = http.Items["TenantPathCode"] as string,
})).AllowAnonymous();
app.MapHub<iucs.meettomanage.api.Hubs.ClassroomHub>("/hubs/classroom");
app.MapHub<iucs.meettomanage.api.Hubs.MonitoringHub>("/hubs/monitoring");

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestampUtc = DateTime.UtcNow }));

// Deliberately outside /api and unauthenticated -- the whole point of a short link is that
// whoever receives it (a parent with no account, on any device) can just tap it. Redirects
// straight to the real target (e.g. a personal Jitsi join URL with its own signed token)
// rather than round-tripping through the SPA first.
app.MapGet("/m/{slug}", async (
    string slug,
    iucs.meettomanage.application.Services.IShortLinkService shortLinks,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    // A share channel's own link-preview bot (WhatsApp/iMessage/email client fetching this URL
    // the instant it's pasted, to build a preview card) or an intermediary proxy has no business
    // caching this redirect -- the whole point of resolving through here on every click (rather
    // than handing out the final target directly) is that it stays live no matter how long ago
    // it was shared. Without an explicit no-store, a cached hop here would freeze whoever
    // actually taps the link onto whatever was resolved at share time instead of click time.
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";

    var target = await shortLinks.ResolveAsync(slug, cancellationToken);
    return target is null
        ? Results.NotFound("This link has expired or doesn't exist.")
        : Results.Redirect(target, permanent: false);
});

if (tenantMode) await app.InitializeTenantsAsync();
else await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

// The S3-or-local choice saved on Admin → Settings → File storage.
if (!tenantMode) await app.Services.GetRequiredService<StorageProviderSwitch>().LoadAsync(app.Services);

// Browser-direct (multipart) uploads need the bucket itself to allow the portal's origin. Done
// whenever S3 is configured, so switching to it from the Settings page works straight away.
if (s3Storage is not null)
{
    _ = Task.Run(() => s3Storage.EnsureBrowserUploadCorsAsync(
        allowedOrigins, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BucketCors")));
}

app.Run();
