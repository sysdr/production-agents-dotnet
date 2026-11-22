# Profiling Notes — Post 28
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# WORKFLOW: NBomber finds WHERE latency degrades. dotnet-trace finds WHY.
#
# ── STEP 1: dotnet-counters (zero-overhead live view) ──────────────────────
#
# Run alongside the 200 RPS NBomber load step. No trace file, no overhead.
# Shows GC, threadpool, requests/sec, memory — all in real time.
#
#   dotnet-counters monitor --name OrderService --counters \
#     System.Runtime,Microsoft.AspNetCore.Hosting
#
# KEY COUNTERS TO WATCH:
#   cpu-usage                    → should be < 80% at 200 RPS
#   threadpool-thread-count      → sudden jump = threadpool starvation
#   threadpool-queue-length      → > 0 sustained = work items queueing
#   gc-heap-size                 → growing = memory leak or allocation pressure
#   alloc-rate                   → > 50 MB/s at 200 RPS = hot allocation path
#   requests-per-second          → cross-check against NBomber achieved RPS
#   requests-failed-per-second   → > 0 = errors, check which status code
#   current-requests             → high value = requests not completing
#
# ── STEP 2: dotnet-trace (capture during peak load) ────────────────────────
#
#   # Terminal 1: start load at 200 RPS (Post 27 NBomber, step 3 only)
#   dotnet run --project tests/LoadTests -- --orderServiceUrl http://localhost:PORT \
#     --userServiceUrl http://localhost:UPORT
#
#   # Terminal 2: capture a 30-second trace during peak load
#   dotnet-trace collect --name OrderService --duration 00:00:30 \
#     --output docs/order-service-200rps.nettrace \
#     --providers Microsoft-DotNETRuntime:0x1F000080018:4
#
#   # Providers breakdown:
#   #   Microsoft-DotNETRuntime with flags:
#   #     GC (0x1)           — GC events and allocations
#   #     JIT (0x10)         — JIT compilation events (omit after warm-up)
#   #     Loader (0x8)       — assembly loading
#   #     Exception (0x8000) — all exceptions thrown
#   #     Threading (0x10000)— threadpool and task events
#   #   Level 4 (Verbose)
#
# ── STEP 3: PerfView analysis (Windows) ────────────────────────────────────
#
#   PerfView.exe order-service-200rps.nettrace
#
#   WHAT TO LOOK AT:
#   1. CPU Stacks → CPU_TIME → order-service process
#      → Flame graph (or "By Name" list sorted by Incl %)
#      → Top entry = the function where CPU time accumulates
#
#   2. Common hot paths in this series at 200 RPS:
#
#      a) TenantConfigStore.GetTenantConfigAsync
#         → TableClient.GetEntityAsync
#         → Every request hits Table Storage (no caching)
#         → Fix: add IMemoryCache with 30s TTL per tenantId
#
#      b) JwtBearerHandler.HandleAuthenticateAsync
#         → HMAC-SHA256 signature verification
#         → Normally fast (< 0.5ms) but shows up at high RPS
#         → Fix: none needed; JWT validation is expected overhead
#
#      c) ServiceBusSender.SendMessageAsync
#         → Waiting on AMQP connection slot
#         → Shows as async wait, not CPU burn
#         → Visible in "Thread Time" view, not CPU view
#         → Fix: pre-warm connection pool or use batched send
#
# ── STEP 4: SpeedScope (cross-platform alternative to PerfView) ────────────
#
#   # Convert .nettrace to SpeedScope JSON
#   dotnet-trace convert docs/order-service-200rps.nettrace \
#     --format Speedscope --output docs/order-service-speedscope.json
#
#   # Open in browser (no install needed)
#   # Upload to https://www.speedscope.app or run locally:
#   # npx speedscope docs/order-service-speedscope.json
#
#   SpeedScope shows "Left Heavy" flame graph by default.
#   Same data as PerfView CPU Stacks, different UI.
#   Useful on macOS/Linux where PerfView is unavailable.
#
# ── READING THE FLAME GRAPH ─────────────────────────────────────────────────
#
#   Each bar = one function on the call stack
#   Width = % of samples that included this function
#   Height = call depth (bottom = root, top = leaf)
#
#   LOOK FOR:
#   - Wide flat bars at the top = leaf function consuming CPU (hottest code)
#   - Wide bars in the middle = code that calls many different things
#   - The same frame appearing across many distinct stacks = shared bottleneck
#
#   WHAT TO IGNORE:
#   - .NET runtime internals (System.Private.CoreLib)
#     unless they dominate (> 30% width = allocation pressure)
#   - Garbage collection (GC stacks) unless > 15% of samples
#
# ── OPTIMIZATION PRIORITIES ─────────────────────────────────────────────────
#
#   After this series reaches AKS (Phase 9), the profiling workflow is:
#   1. NBomber → find ceiling RPS (Post 27)
#   2. dotnet-trace → find hot path at ceiling (Post 28)
#   3. Fix the hot path
#   4. Re-run NBomber → confirm ceiling increased
#   5. Repeat until p99 SLA is met at target RPS
#
#   The most common fix in this codebase: add MemoryCache to TenantConfigStore.
#   One cache line: builder.Services.AddMemoryCache();
#   One wrapper in TenantConfigStore.GetTenantConfigAsync → reduces p99 by ~40% at 200 RPS.
