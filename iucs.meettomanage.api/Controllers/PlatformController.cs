using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using iucs.meettomanage.api.Auth;
using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.application.Common.Exceptions;
using iucs.meettomanage.application.Common.Interfaces;
using iucs.meettomanage.domain.Data;
using iucs.meettomanage.domain.Entities.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace iucs.meettomanage.api.Controllers;

[ApiController, Route("api/platform")]
public sealed class PlatformController(IConfiguration configuration, IPasswordHasher hasher,
    IOptions<JwtOptions> jwt, MasterDbContext master, TenantProvisioner provisioner, TenantScopeFactory scopes,
    SaasSubscriptionService subscriptions, TenantDomainResolver domains) : ControllerBase
{
    [HttpPost("tenants/{id:guid}/synchronize-logins"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> SynchronizeLogins(Guid id, CancellationToken ct)
    {
        var tenant = await master.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Tenant", id);
        if (tenant.Status != TenantStatus.Suspended)
            throw new ConflictException("Suspend the client before synchronizing imported users.");
        using var scope = scopes.CreateScope(tenant);
        var skippedPhones = await scope.ServiceProvider.GetRequiredService<LoginDirectory>().RebuildAsync(ct);
        return Ok(new { skippedPhones });
    }
    public sealed class PlatformLogin
    {
        [Required] public string Email { get; set; } = "";
        [Required] public string Password { get; set; } = "";
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public IActionResult Login(PlatformLogin request)
    {
        var email = configuration["MultiTenancy:PlatformAdmin:Email"];
        var hash = configuration["MultiTenancy:PlatformAdmin:PasswordHash"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(hash)
            || !string.Equals(request.Email.Trim(), email, StringComparison.OrdinalIgnoreCase)
            || !hasher.Verify(request.Password, hash)) throw new UnauthorizedException("Invalid platform credentials.");
        var options = jwt.Value;
        var expires = DateTime.UtcNow.AddMinutes(30);
        var token = new JwtSecurityToken(options.Issuer, options.Audience,
            [new("purpose", "platform-admin"), new(ClaimTypes.Role, "PlatformAdmin"), new(ClaimTypes.Name, email)],
            DateTime.UtcNow, expires, new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256));
        return Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc = expires });
    }

    [HttpGet("tenants"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await master.Tenants.AsNoTracking()
        .OrderBy(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Code, x.Name, x.CustomDomain, x.CustomDomainVerifiedAtUtc, x.Status, x.CreatedAtUtc, x.ProvisionedAtUtc, x.AppAccessGranted }).ToListAsync(ct));

    public sealed class SetCustomDomainRequest
    {
        [MaxLength(253)] public string? Domain { get; set; }
    }

    [HttpPut("tenants/{id:guid}/custom-domain"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> SetCustomDomain(Guid id, SetCustomDomainRequest request, CancellationToken ct)
    {
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        var domain = TenantDomainResolver.Normalize(request.Domain);
        if (domain is not null && domains.ReservedHosts().Contains(domain))
            throw new DomainValidationException("That hostname is reserved by this deployment.");
        if (domain is not null && await master.Tenants.AnyAsync(x => x.Id != id && x.CustomDomain == domain, ct))
            throw new ConflictException("That custom domain is already assigned to another tenant.");
        var previous = tenant.CustomDomain;
        tenant.CustomDomain = domain;
        tenant.CustomDomainVerifiedAtUtc = domain == previous ? tenant.CustomDomainVerifiedAtUtc : null;
        domains.Invalidate(previous); domains.Invalidate(domain);
        var sub = await master.TenantSubscriptions.SingleOrDefaultAsync(x => x.TenantId == id, ct);
        if (sub is not null) master.TenantSubscriptionEvents.Add(new()
        {
            TenantId = id, SubscriptionId = sub.Id, EventType = "custom_domain_changed",
            Detail = $"{previous ?? "(none)"} -> {domain ?? "(none)"}", Actor = "platform:" + User.Identity?.Name,
        });
        try { await master.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("That custom domain was assigned by another request. Refresh and try again."); }
        return NoContent();
    }

    [HttpPost("tenants/{id:guid}/custom-domain/verify"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> VerifyCustomDomain(Guid id, CancellationToken ct)
    {
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        if (tenant.CustomDomain is null) throw new DomainValidationException("Save a custom domain before verifying it.");
        var target = Uri.TryCreate(configuration["Frontend:BaseUrl"], UriKind.Absolute, out var frontend) ? frontend.IdnHost : null;
        if (target is null) throw new InvalidOperationException("Frontend:BaseUrl must be configured before domains can be verified.");
        System.Net.IPAddress[] customAddresses, targetAddresses;
        try
        {
            customAddresses = await System.Net.Dns.GetHostAddressesAsync(tenant.CustomDomain, ct);
            targetAddresses = await System.Net.Dns.GetHostAddressesAsync(target, ct);
        }
        catch (System.Net.Sockets.SocketException) { throw new ConflictException("DNS is not ready yet. Point the custom domain to this deployment and try again."); }
        if (!customAddresses.Intersect(targetAddresses).Any())
            throw new ConflictException("The custom domain does not resolve to this deployment yet.");
        tenant.CustomDomainVerifiedAtUtc = DateTime.UtcNow;
        await master.SaveChangesAsync(ct);
        domains.Invalidate(tenant.CustomDomain);
        return Ok(new { tenant.CustomDomain, tenant.CustomDomainVerifiedAtUtc });
    }

    [HttpPost("tenants"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = await provisioner.CreateAsync(request, ct);
        await StartSubscriptionAsync(tenant, request, ct);
        return Ok(new { tenant.Id, tenant.Code, tenant.Name, tenant.Status, tenant.AppAccessGranted });
    }

    [HttpPost("tenants/{id:guid}/retry"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> Retry(Guid id, CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = await provisioner.ProvisionAsync(id, request, ct);
        await StartSubscriptionAsync(tenant, request, ct);
        return Ok(new { tenant.Id, tenant.Code, tenant.Name, tenant.Status });
    }

    /// <summary>A newly provisioned tenant gets a platform subscription: the requested plan, else a trial (new database) or legacy (attached database).</summary>
    private Task StartSubscriptionAsync(Tenant tenant, CreateTenantRequest request, CancellationToken ct) =>
        subscriptions.EnsureSubscriptionAsync(tenant.Id,
            request.PlanCode ?? (string.IsNullOrWhiteSpace(request.DatabaseName) ? null : SaasSeeder.LegacyPlanCode),
            "platform:" + User.Identity?.Name, request.AdminEmail, tenant.Name, ct);

    public sealed class SetAppAccessRequest { public bool Granted { get; set; } }

    /// <summary>Grants or withdraws the tenant's access for the mobile parent app. Takes effect on the next request.</summary>
    [HttpPut("tenants/{id:guid}/app-access"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> SetAppAccess(Guid id, SetAppAccessRequest request, CancellationToken ct)
    {
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        tenant.AppAccessGranted = request.Granted;
        await master.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed class SetStatusRequest { public bool Suspended { get; set; } }
    [HttpPut("tenants/{id:guid}/status"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> SetStatus(Guid id, SetStatusRequest request, CancellationToken ct)
    {
        var tenant = await master.Tenants.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
        if (tenant.Status is not (TenantStatus.Active or TenantStatus.Suspended))
            throw new ConflictException("Provision the tenant before changing its status.");
        tenant.Status = request.Suspended ? TenantStatus.Suspended : TenantStatus.Active;
        await master.SaveChangesAsync(ct);
        return NoContent();
    }
}
