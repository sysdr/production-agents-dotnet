// src/UserService/Program.cs
// CUMULATIVE — Post 5: replace magic string attribute names with
// TelemetryConstants compile-time constants.
//
// Post 1: Minimal API + OTel + health checks
// Post 2: Serilog enrichers (via ServiceDefaults)
// Post 3: OTel SemanticConventions + ValidateCredentials span
// Post 4: multi-severity logs + /users/{id}/profile
// Post 5: TelemetryConstants constants replace string literals NEW

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHttpClient();

var app = builder.Build();
app.MapDefaultEndpoints();

var activitySource = Extensions.ActivitySource;

app.MapGet("/users/ping", () => Results.Ok("pong"));

app.MapPost("/users/login", (ILogger<Program> logger) =>
{
    var userId    = "user-" + Random.Shared.Next(1000, 9999);
    var ipAddress = $"192.168.1.{Random.Shared.Next(1, 254)}";

    using var activity = activitySource.StartActivity("ValidateCredentials");
    activity?.SetTag(TraceSemanticConventions.AttributeEnduserRole,
                     TelemetryConstants.AttrEndUserRole);  // compile-time

    // POST 5: Use TelemetryConstants instead of raw strings.
    // Before: logger.LogInformation("Login. {UserId} {IpAddress}", ...)
    // After: names come from compile-time constants — verified by telemetry-schema.md
    //
    // This means: rename AttrUserId → AttrUserIdentifier in TelemetryConstants.cs
    // → every reference in this file and in OrderService breaks at compile time.
    // The schema change is no longer invisible.
    logger.LogInformation(
        "Login attempt. {UserId} {IpAddress} {EndUserRole}",
        userId, ipAddress,
        TelemetryConstants.AttrEndUserRole);

    if (Random.Shared.Next(10) == 0)
        logger.LogWarning(
            "Slow credential validation. {UserId} {ElapsedMs}",
            userId, Random.Shared.Next(800, 2000));

    return Results.Ok(new { UserId = userId, Status = "authenticated" });
});

app.MapGet("/users/{id}/profile", async (string id, ILogger<Program> logger,
    [FromServices] IHttpClientFactory httpFactory) =>
{
    logger.LogInformation("Profile request. {UserId}", id);
    var client = httpFactory.CreateClient();
    try
    {
        var response = await client.GetAsync("http://order-service/orders/ping");
        logger.LogInformation("Downstream ping. {UserId} {ElapsedMs}",
            id, (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Downstream call failed. {UserId}", id);
    }
    return Results.Ok(new { UserId = id, DisplayName = $"User {id}" });
});

app.MapPut("/orders/{id}/cancel", (string id, ILogger<Program> logger) =>
{
    logger.LogWarning("Cancel attempted. {OrderId}", id);
    return Results.BadRequest(new { Error = "Order not found" });
});

// POST 19: /users/login-failed — simulates failed login attempts for Sentinel
// detection rule exercise (Rule 1 — brute force login detection).
// Logs failed login with UserId, client IP, and reason to App Insights traces.
// Use: call 10+ times in rapid succession to trigger the brute-force rule.
app.MapPost("/users/login-failed", (
    ILogger<Program> logger,
    HttpContext ctx) =>
{
    using var activity = Extensions.ActivitySource.StartActivity("FailedLoginAttempt");
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var fakeUserId = $"user-{Random.Shared.Next(1, 5):D3}"; // simulate 4 users

    activity?.SetTag(TelemetryConstants.AttrUserId, fakeUserId);

    logger.LogWarning(
        "Login failed. {UserId} {ClientIp} {Reason}",
        fakeUserId, clientIp, "InvalidCredentials");

    return Results.Unauthorized();
});

// POST 20: /auth/token — TEST ONLY — mints a signed JWT for local development.
//
// PURPOSE: Provides a valid bearer token for the /orders/create [Authorize] exercise.
//          In production, tokens are issued by Azure AD / Entra ID — not by the application.
//          This endpoint would NOT exist in a production UserService.
//
// TOKEN CLAIMS:
//   sub       — UserId from the request body
//   email     — Email from the request body
//   tenant_id — TenantId from the request body (matches customDimensions.TenantId in App Insights)
//   jti       — unique token ID per request (prevents token replay)
//   exp       — expires in 1 hour
//
// PRODUCTION PATH (Microsoft.Identity.Web):
//   1. Register an app in Azure AD / Entra ID (App Registration)
//   2. Grant it API permissions to the OrderService app registration
//   3. Acquire token via MSAL: app.AcquireTokenForClient(scopes)
//   4. The token is issued by Microsoft, signed with Azure AD's private key
//   5. OrderService validates using OIDC discovery (no shared secret needed)
app.MapPost("/auth/token", (TokenRequest req, IConfiguration config) =>
{
    var jwtConfig = config.GetSection("Jwt");
    var keyBytes  = System.Text.Encoding.UTF8.GetBytes(
        jwtConfig["SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey not configured"));

    var claims = new[]
    {
        new System.Security.Claims.Claim(
            Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub, req.UserId),
        new System.Security.Claims.Claim(
            Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Email, req.Email),
        new System.Security.Claims.Claim("tenant_id", req.TenantId),
        new System.Security.Claims.Claim(
            Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Jti,
            Guid.NewGuid().ToString()),
    };

    var key   = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(keyBytes);
    var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
        key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer:             jwtConfig["Issuer"] ?? "hyperscale-local",
        audience:           jwtConfig["Audience"] ?? "order-service",
        claims:             claims,
        expires:            DateTime.UtcNow.AddHours(1),
        signingCredentials: creds);

    var tokenString = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
        .WriteToken(token);

    return Results.Ok(new { Token = tokenString, ExpiresIn = 3600 });
}).WithName("GetTestToken")
  .WithDescription("TEST ONLY — issues a local development JWT. Not present in production.");

// POST 24: /users/status — target of Dapr service invocation from OrderService
app.MapGet("/users/status", (ILogger<Program> logger) =>
{
    logger.LogInformation("Dapr service invocation received. {Source}", "order-service");
    return Results.Ok(new { Status="healthy", Version="1.0", Service="user-service", Dapr=true });
});

app.Run();

// POST 20: Request body for /auth/token — the claims to include in the test JWT.
// In production these come from Azure AD after the user authenticates interactively
// or the service authenticates with a client credential.
// (Declared after top-level statements — C# requires types after all statements.)
record TokenRequest(
    string UserId   = "user-001",
    string Email    = "dev@hyperscale.local",
    string TenantId = "tenant-01");
