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
    SaasSubscriptionService subscriptions) : ControllerBase
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
        .OrderBy(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Code, x.Name, x.Status, x.CreatedAtUtc, x.ProvisionedAtUtc }).ToListAsync(ct));

    [HttpPost("tenants"), Authorize(Policy = "PlatformAdmin")]
    public async Task<IActionResult> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = await provisioner.CreateAsync(request, ct);
        await StartSubscriptionAsync(tenant, request, ct);
        return Ok(new { tenant.Id, tenant.Code, tenant.Name, tenant.Status });
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
