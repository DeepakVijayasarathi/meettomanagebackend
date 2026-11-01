using iucs.meettomanage.api.Saas;
using iucs.meettomanage.api.Tenancy;
using iucs.meettomanage.domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace iucs.meettomanage.api.Auth
{
    public class PermissionRequirement : IAuthorizationRequirement
    {
        public PermissionRequirement(string permission)
        {
            Permission = permission;
        }

        /// <summary>"Module:Action" string matching the claims issued at login.</summary>
        public string Permission { get; }
    }

    /// <summary>
    /// Grants a "Module:Action" requirement to Admins and to users holding the matching claim,
    /// unless the tenant's plan switches that module off (e.g. Finance on the Free plan), which
    /// applies to Admins too. The plan check fails open if billing data can't be read.
    /// </summary>
    public class PermissionAuthorizationHandler(IHttpContextAccessor http,
        ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
    {
        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            var granted = context.User.IsInRole(nameof(UserRole.Admin))
                || context.User.HasClaim(JwtTokenService.PermissionClaimType, requirement.Permission);
            if (!granted) return;

            if (await ModuleInPlanAsync(context.User, requirement.Permission.Split(':')[0]))
            {
                context.Succeed(requirement);
            }
        }

        private async Task<bool> ModuleInPlanAsync(System.Security.Claims.ClaimsPrincipal user, string module)
        {
            var httpContext = http.HttpContext;
            // Authorization runs before TenantRequestMiddleware binds the context, so the
            // (already validated) tenant claim is the primary source.
            // Some endpoints (e.g. the admin home dashboard) must work on every plan.
            if (httpContext?.GetEndpoint()?.Metadata.GetMetadata<AnyPlanAttribute>() is not null) return true;
            Guid? claimed = Guid.TryParse(user.FindFirst(TenantContext.TenantIdClaim)?.Value, out var id) ? id : null;
            if ((claimed ?? httpContext?.RequestServices.GetService<TenantContext>()?.TenantId) is not { } tenantId) return true;
            // SaaS services exist only in multi-tenant mode.
            if (httpContext?.RequestServices.GetService<IEntitlementService>() is not { } entitlements) return true;
            try
            {
                var plan = await entitlements.GetAsync(tenantId, httpContext.RequestAborted);
                return plan.HasModule(EntitlementKeys.ModuleKey(module));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Plan modules could not be read for tenant {TenantId}; allowing the request.", tenantId);
                return true;
            }
        }
    }

    /// <summary>
    /// Keeps the role/permission check on an endpoint but skips the plan's module switch, for
    /// screens every plan needs (the admin home dashboard reads its numbers from Reports).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class AnyPlanAttribute : Attribute
    {
    }

    /// <summary>
    /// Materialises "perm:Module:Action" policies on demand so each module/action
    /// pair doesn't need manual registration.
    /// </summary>
    public class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
            : base(options)
        {
        }

        public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
            {
                var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];
                return new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission))
                    .Build();
            }

            return await base.GetPolicyAsync(policyName);
        }
    }
}
