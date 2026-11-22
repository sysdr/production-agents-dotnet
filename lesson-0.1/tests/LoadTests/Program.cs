// tests/LoadTests/Program.cs
// POST 27: NBomber load test for POST /orders/create
//
// WHAT THIS MEASURES:
//   The end-to-end latency of creating an order through the full Phase 1-5 stack:
//   HTTP → JwtBearer auth → TenantAuthorizationHandler (Table Storage) → ServiceBus publish
//   → Event Hubs publish. All components run locally via emulators.
//
// THREE-STEP LOAD PLAN:
//   Step 1: 50 RPS for 30s  — baseline (should be well within emulator capacity)
//   Step 2: 100 RPS for 30s — moderate load
//   Step 3: 200 RPS for 30s — high load (likely to surface bottlenecks)
//
// WHAT TO LOOK FOR IN THE REPORT:
//   p50 latency:  should remain < 200ms at all steps
//   p99 latency:  watch for jump between steps — indicates resource contention
//   Error rate:   > 1% at 200 RPS may indicate emulator saturation
//   RPS achieved: if achieved RPS < requested RPS, the client is the bottleneck
//
// HOW TO RUN:
//   First get the OrderService port from Aspire Dashboard:
//     $port = (Invoke-RestMethod http://localhost:15888/api/v1/resources |
//              Where-Object { $_.name -eq "order-service" }).services[0].allocatedEndpoint.port
//     dotnet run --project tests/LoadTests -- --orderServiceUrl "http://localhost:$port"
//                                             --userServiceUrl  "http://localhost:$userPort"
//
// BRIDGE FROM FAMILIAR:
//   Apache JMeter (.jmx test plans) and Visual Studio Load Test (.loadtest files) are
//   the classic .NET equivalents. NBomber is code-first — the load plan is C#, not XML.
//   This means the load test can reference the same Contracts types as the application.

using System.Net.Http.Json;
using System.Text.Json;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Http.CSharp;

// ── Parse arguments ──────────────────────────────────────────────────────────
// Lesson 0.1: top-level programs already expose `args`; redeclaring it is CS0136.
string orderServiceUrl = GetArg(args, "--orderServiceUrl", "http://localhost:5000");
string userServiceUrl  = GetArg(args, "--userServiceUrl",  "http://localhost:5001");
string reportPath      = GetArg(args, "--reportPath",      "docs/nbomber-report");

Console.WriteLine($"NBomber load test — Post 27");
Console.WriteLine($"  OrderService: {orderServiceUrl}");
Console.WriteLine($"  UserService:  {userServiceUrl}");
Console.WriteLine($"  Report:       {reportPath}/");

// ── Acquire a long-lived JWT token ───────────────────────────────────────────
// POST 27: Get a JWT once before the load test starts.
// Reusing one token for all requests avoids measuring token acquisition latency
// (which is not the bottleneck we are investigating).
// Token lifetime is 1 hour (Post 20) — well within a typical 3-minute load run.
using var setupClient = new HttpClient { BaseAddress = new Uri(userServiceUrl) };

TokenResponse? tokenResponse = null;
try
{
    tokenResponse = await setupClient.PostAsJsonAsync("/auth/token",
        new { UserId = "load-test-user", Email = "load@test.com", TenantId = "tenant-01" })
        .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<TokenResponse>().Result);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to acquire JWT: {ex.Message}");
    Console.Error.WriteLine("Ensure Aspire is running and UserService is healthy.");
    return 1;
}

var bearerToken = tokenResponse!.Token;
Console.WriteLine($"  JWT acquired ({bearerToken[..40]}...)");

// ── Define the scenario ───────────────────────────────────────────────────────
// POST 27: One HttpClient per scenario — shared across all virtual users.
// HttpClient is thread-safe; HttpClientHandler manages connection pooling.
// Connection pool size defaults to Environment.ProcessorCount * 8.
using var httpClient = new HttpClient
{
    BaseAddress = new Uri(orderServiceUrl),
    DefaultRequestHeaders = { Authorization = new("Bearer", bearerToken) },
    Timeout = TimeSpan.FromSeconds(10),
};

// POST 27: Scenario.Create — the unit of load in NBomber.
// The async function runs once per simulated user per interval.
// It must return Response.Ok() or Response.Fail() to count in the report.
var orderCreateScenario = Scenario.Create("order_create_post27", async context =>
{
    // POST 27: HttpClientScenario uses the typed response for latency measurement.
    // NBomber starts timing before the await and stops when the response is received.
    var response = await httpClient.PostAsync("/orders/create",
        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

    return response.IsSuccessStatusCode
        // Lesson 0.1 (NBomber 6): status codes are strings and Fail takes `message`.
        ? Response.Ok(statusCode: ((int)response.StatusCode).ToString())
        : Response.Fail(statusCode: ((int)response.StatusCode).ToString(),
                        message:    $"HTTP {(int)response.StatusCode}");
})
// POST 27: Warm-up — 5 seconds at a low rate before measurement starts.
// Allows JIT compilation, connection pool establishment, and Table Storage
// TenantConfigStore cache warm-up. Without warm-up, the first data point
// includes first-JIT latency and cold connection overhead — not representative.
.WithWarmUpDuration(TimeSpan.FromSeconds(5))
// POST 27: The three load steps — stepped injection model.
// Simulation.Inject(rate, interval, during):
//   rate=50, interval=1s → inject 50 new users every 1 second = 50 RPS
//   rate=100 → 100 RPS
//   rate=200 → 200 RPS
// POST 27 principle: stepped load reveals the RPS at which latency grows non-linearly.
// A p99 that triples between step 2 and step 3 indicates a bottleneck at ~100 RPS.
.WithLoadSimulations(
    Simulation.Inject(rate: 50,  interval: TimeSpan.FromSeconds(1),
                      during: TimeSpan.FromSeconds(30)),
    Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1),
                      during: TimeSpan.FromSeconds(30)),
    Simulation.Inject(rate: 200, interval: TimeSpan.FromSeconds(1),
                      during: TimeSpan.FromSeconds(30)));

// ── Run the load test ─────────────────────────────────────────────────────────
// POST 27: NBomberRunner.RegisterScenarios → Run() executes the load plan.
// ReportFolder and ReportFormats control the report output.
// The HTML report includes a latency histogram, RPS timeline, and error log.
NBomberRunner
    .RegisterScenarios(orderCreateScenario)
    .WithReportFolder(reportPath)
    .WithReportFormats(
        ReportFormat.Html,
        ReportFormat.Md,
        ReportFormat.Csv)
    .Run();

Console.WriteLine($"\nReport written to {reportPath}/");
Console.WriteLine("Open the .html file in a browser to see the full latency breakdown.");
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static string GetArg(string[] args, string key, string defaultValue)
{
    var idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultValue;
}

record TokenResponse(string Token, int ExpiresIn);
