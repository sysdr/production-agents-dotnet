// src/OrderService/TenantAuthorizationRequirement.cs
// POST 21: Custom authorization policy — RequireEnabledTenant.
//
// AUTHORIZATION POLICY TYPES IN ASP.NET CORE:
//
//   1. Role-based     [Authorize(Roles = "Admin")]
//      Checks a "roles" claim in the JWT. Simple but coarse-grained.
//      Works without any custom code. Limit: roles are defined at token issue time,
//      not at request time — disabled tenant still has their role until token expires.
//
//   2. Claims-based   options.AddPolicy("P", p => p.RequireClaim("tenant_id"))
//      Checks that a claim exists in HttpContext.User.Claims.
//      Still static — no runtime business logic lookup.
//
//   3. Resource-based (this file)
//      IAuthorizationRequirement + AuthorizationHandler<TRequirement>
//      Injects services (ILogger, TenantConfigStore) via DI.
//      Can query Table Storage, Blob Storage, or any data source at authorization time.
//      The handler's Succeed() / Fail() decision is the gate.
//
// WHY RESOURCE-BASED FOR THIS USE CASE:
//   The `tenant_id` claim in the JWT is set at token issue time (Post 20 /auth/token).
//   A tenant can be disabled in TenantConfigStore at any time — but the existing JWT
//   is valid until its `exp` claim. Without a runtime check, a disabled tenant's
//   token still creates orders.
//   This handler reads TenantConfigStore on every protected request. If IsEnabled = false,
//   the request gets 403 Forbidden — even with a valid JWT.
//
// THREE-TYPE PATTERN (required by ASP.NET Core):
//   1. IAuthorizationRequirement  — marker interface, carries no data here
//   2. AuthorizationHandler<T>    — contains the validation logic, injected via DI
//   3. services.AddAuthorization  — binds requirement to policy name

using Microsoft.AspNetCore.Authorization;

// Step 1: The requirement — a marker that identifies this policy type.
// Carries no data in this case; the tenant_id is read from HttpContext.User.
public class RequireEnabledTenantRequirement : IAuthorizationRequirement { }

// Step 2: The handler — contains the actual business logic.
// Injected by ASP.NET Core DI; TenantConfigStore is resolved from the container.
public class TenantAuthorizationHandler
    : AuthorizationHandler<RequireEnabledTenantRequirement>
{
    private readonly TenantConfigStore _tenantConfigStore;
    private readonly ILogger<TenantAuthorizationHandler> _logger;

    public TenantAuthorizationHandler(
        TenantConfigStore tenantConfigStore,
        ILogger<TenantAuthorizationHandler> logger)
    {
        _tenantConfigStore = tenantConfigStore;
        _logger            = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequireEnabledTenantRequirement requirement)
    {
        // POST 21: Extract tenant_id from JWT claims.
        // Set by OnTokenValidated (Post 20 AuthConfig.cs) as an OTel tag.
        // The claim name "tenant_id" matches the name used in /auth/token (UserService).
        // In production (Azure AD), this would be a custom claim added via an
        // optional claims configuration on the app registration.
        var tenantId = context.User.FindFirst("tenant_id")?.Value;

        if (string.IsNullOrEmpty(tenantId))
        {
            // POST 21: No tenant_id claim → deny.
            // A valid JWT without a tenant_id claim cannot create orders —
            // it is a machine-to-machine token or a token from a different issuer.
            _logger.LogWarning("Authorization failed: no tenant_id claim in JWT");
            context.Fail();
            return;
        }

        // POST 21: Look up tenant configuration in Azure Table Storage (Azurite locally).
        // This is the runtime business-logic check that a claims-based policy cannot do.
        var config = await _tenantConfigStore.GetTenantConfigAsync(tenantId);

        if (config is null)
        {
            // Unknown tenant — not in TenantConfigStore.
            // Default-deny: if a tenant is not in the table, it cannot create orders.
            // The seed data in TenantConfigStore.EnsureTableExistsAsync (Post 17)
            // creates entries for tenant-01, tenant-02, tenant-03.
            _logger.LogWarning(
                "Authorization failed: tenant {TenantId} not found in config store", tenantId);
            context.Fail();
            return;
        }

        if (!config.IsEnabled)
        {
            // POST 21: Tenant is disabled — explicit deny.
            // The JWT is valid. The `exp` claim has not elapsed.
            // But the runtime business rule overrides the token validity.
            _logger.LogWarning(
                "Authorization failed: tenant {TenantId} is disabled in config store", tenantId);
            context.Fail();
            return;
        }

        // POST 21: Tenant is known and enabled. 
        // Must call Succeed() — handlers that return without calling Succeed() 
        // default to Fail() when no other handler succeeds the requirement.
        _logger.LogDebug("Authorization passed: tenant {TenantId} enabled", tenantId);
        context.Succeed(requirement);
    }
}
