// src/OrderService/AuthConfig.cs
// POST 20: JWT bearer authentication configuration.
//
// LOCAL DEVELOPMENT (this file):
//   Uses a symmetric HMAC-SHA256 signing key stored in appsettings.Development.json.
//   No Azure subscription, no Azure AD tenant required.
//   The same JwtBearer middleware runs; only the key source differs.
//
// PRODUCTION — Microsoft.Identity.Web path:
//   Replace AddJwtBearer() with builder.Services.AddMicrosoftIdentityWebApiAuthentication(config)
//   Add "AzureAd" section to appsettings.json (TenantId, ClientId, Audience).
//   Tokens issued by Azure AD / Entra ID are validated using OIDC discovery (no key to manage).
//   Install: Microsoft.Identity.Web (wraps JwtBearer for Azure AD).
//   One configuration change, no code change.
//
// JWT CLAIMS used in this series:
//   sub          — userId (set from TokenRequest.UserId)
//   email        — user email
//   tenant_id    — tenantId (matches TelemetryConstants.AttrTenantId from Post 3)
//   jti          — unique token ID (for replay detection)
//
// BRIDGE FROM FAMILIAR:
//   WCF wsHttpBinding with <security mode="Message"> and userName tokens
//   is the WCF equivalent of this pattern: the token is presented per-request,
//   validated by the receiver, and claims are extracted from it.
//   The difference: WCF tokens are XML-based, SOAP-layer; JWT tokens are
//   base64url JSON, HTTP-layer (Authorization header).

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using ServiceDefaults;

public static class AuthConfig
{
    public static IServiceCollection AddJwtAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwt = configuration.GetSection("Jwt");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // POST 20: SymmetricSecurityKey — the shared secret between
                // the token issuer (UserService /auth/token) and the validator (OrderService).
                // In production with Azure AD, this is replaced by public key discovery:
                //   options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0"
                //   options.Audience  = clientId
                // Azure AD publishes its public keys at the OIDC discovery endpoint;
                // JwtBearer fetches and caches them automatically.
                var keyBytes = Encoding.UTF8.GetBytes(
                    jwt["SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey not configured"));

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = new SymmetricSecurityKey(keyBytes),
                    ValidateIssuer           = true,
                    ValidIssuer              = jwt["Issuer"] ?? "hyperscale-local",
                    ValidateAudience         = true,
                    ValidAudience            = jwt["Audience"] ?? "order-service",
                    ValidateLifetime         = true,
                    ClockSkew                = TimeSpan.FromMinutes(1),
                };

                // POST 20: Wire OTel traces to JWT validation events.
                // OnAuthenticationFailed → OTel span tag (appears in Aspire Dashboard).
                // Useful for diagnosing "why did this request get a 401?" without
                // adding application logging that would include token content.
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        // Tag the current span with the failure reason.
                        // Do NOT log the token itself — it may contain PII.
                        System.Diagnostics.Activity.Current?
                            .SetTag("auth.failure", ctx.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        // Extract tenant_id claim and set on OTel span.
                        // This makes tenant context visible in Aspire traces even
                        // before the request handler runs.
                        var tenantId = ctx.Principal?
                            .FindFirst("tenant_id")?.Value;
                        if (tenantId is not null)
                        {
                            System.Diagnostics.Activity.Current?
                                .SetTag(TelemetryConstants.AttrTenantId, tenantId);
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        // POST 21: Register the custom TenantAuthorizationHandler in DI.
        // AuthorizationHandler is scoped because it depends on TenantConfigStore (also scoped).
        // Registered as the abstract base so ASP.NET Core resolves it for all
        // AuthorizationHandler<RequireEnabledTenantRequirement> registrations.
        services.AddScoped<IAuthorizationHandler, TenantAuthorizationHandler>();

        // POST 21: Register the named policy that combines JWT validation + Table Storage check.
        // "RequireEnabledTenant" = valid JWT AND tenant_id exists AND IsEnabled = true.
        // Used as: app.MapPost(...).RequireAuthorization("RequireEnabledTenant")
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireEnabledTenant", policy =>
            {
                // First: require an authenticated user (valid JWT from Post 20)
                policy.RequireAuthenticatedUser();
                // Then: run the custom handler (tenant_id exists + IsEnabled check)
                policy.AddRequirements(new RequireEnabledTenantRequirement());
            });
        });

        return services;
    }
}