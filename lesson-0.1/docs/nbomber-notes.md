# NBomber Load Test — Post 27 Notes
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# HOW TO RUN:
#   1. Start the Aspire stack: dotnet run --project src/AppHost
#   2. Get ports from the Aspire Dashboard:
#        $resources = Invoke-RestMethod http://localhost:15888/api/v1/resources
#        $orderPort = ($resources | Where-Object { $_.name -eq "order-service" }).services[0].allocatedEndpoint.port
#        $userPort  = ($resources | Where-Object { $_.name -eq "user-service"  }).services[0].allocatedEndpoint.port
#   3. Run the load test:
#        dotnet run --project tests/LoadTests -- \
#          --orderServiceUrl "http://localhost:$orderPort" \
#          --userServiceUrl  "http://localhost:$userPort"
#   4. Open docs/nbomber-report/*.html in a browser
#
# INTERPRETING RESULTS:
#
# LATENCY THRESHOLDS (guidelines for local emulator testing):
#   p50 < 100ms   — healthy at all load steps
#   p99 < 500ms   — acceptable; expect it to rise with RPS
#   p99 > 1000ms  — degrading; check which component is bottlenecking
#   error rate > 1% — emulator saturation or connection pool exhausted
#
# COMMON BOTTLENECKS ON LOCAL EMULATORS:
#
#   Service Bus emulator (port 5672):
#     Symptom: p99 rises sharply at 100→200 RPS
#     Diagnosis: check docker stats for servicebus-emulator CPU
#     Fix in prod: scale Service Bus namespace tier (Standard → Premium)
#
#   Table Storage / TenantAuthorizationHandler:
#     Symptom: p99 high even at 50 RPS on first warm-up, normal after
#     Diagnosis: cold connection to Azurite; see warm-up duration in scenario
#     Fix: pre-warm TenantConfigStore connection at startup
#
#   Dapr sidecar (if using /orders/create-dapr):
#     Symptom: higher p99 than direct /orders/create path
#     Diagnosis: sidecar adds a local HTTP hop + Redis pub/sub roundtrip
#     Fix in prod: normal — sidecar overhead is ~1-2ms; Redis is fast
#
#   JWT validation:
#     Symptom: first few requests slow, then normal
#     Diagnosis: HMAC key cache cold on first validation
#     Fix: warm-up period covers this (5s in scenario)
#
# LOAD SIMULATION TYPES (NBomber):
#   Inject(rate, interval, during)  — constant arrival rate (used in Post 27)
#     Closest to real traffic: N new users arrive per interval regardless of system speed.
#     If system is slow, queue builds up. Best for measuring throughput ceiling.
#
#   InjectRandom(minRate, maxRate, interval, during)  — variable arrival (traffic spikes)
#     Simulates traffic bursts. Use after establishing baseline with Inject.
#
#   KeepConstant(copies, during)  — fixed concurrency pool (classic load test model)
#     N virtual users run continuously. Less realistic than Inject for HTTP APIs.
#     Use when testing connection pool exhaustion specifically.
#
# READING THE HTML REPORT:
#   "Latency (ms)" section: p50, p75, p95, p99, p99.9 per scenario step
#   "RPS" chart: achieved vs requested RPS — if achieved < requested, client bottleneck
#   "Errors" table: HTTP status codes returned — 401/403 indicates auth issues
#   Step labels: "order_create_post27" with step index 0/1/2 = 50/100/200 RPS
