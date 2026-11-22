# Capstone 08 — Compound Chaos Engineering: Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 40
#
# ── WHY COMPOUND FAILURE TESTING MATTERS ──────────────────────────────────
#
# Capstones 01-07 each proved ONE resilience mechanism works in isolation:
#   Post 33: NBomber burst        → KEDA scales out
#   Post 34: bad tenant token     → TenantAuthorizationHandler blocks it
#   Post 37: Service Bus outage   → Polly FallbackAction reroutes
#   Post 9:  poison message       → MaxDeliveryCount:3 → DLQ
#
# Each of these tests answers "does mechanism X work?" in isolation.
# Production incidents rarely present one failure at a time. The real
# question senior engineers are asked in incident reviews is:
#   "What happens when your retry storm collides with your queue outage
#    collides with your DLQ filling up, all in the same 60 seconds?"
#
# Compound testing answers a DIFFERENT question than isolated testing:
#   not "does the mechanism work" but "do the mechanisms interfere with
#   each other when active simultaneously."
#
# ── THE FOUR VECTORS AND WHAT THEY STRESS ────────────────────────────────
#
# Vector 1 — Sustained load (NBomber, Post 27):
#   Stresses: connection pools, IMemoryCache (Post 28), thread pool
#   Interacts with: every other vector adds ON TOP of this baseline load
#
# Vector 2 — Poison messages (Post 9):
#   Stresses: MaxDeliveryCount cycle (3 attempts × lock duration),
#             DLQ depth gauge (Post 10), SRE dashboard alert (Post 23)
#   Interacts with: DLQ fills WHILE Service Bus is also degraded (Vector 4) —
#     does dead-lettering still work when the namespace itself is unhealthy?
#
# Vector 3 — Transient failures (Post 9/10):
#   Stresses: Polly retry pipeline (3 attempts inside ProcessMessageAsync)
#   Interacts with: retries add CPU + time pressure DURING the same window
#     poison messages are also retrying — do the two retry cycles compete
#     for the same thread pool resources?
#
# Vector 4 — Service Bus outage (Post 37):
#   Stresses: FallbackServiceBusSender routing to orders-fallback queue
#   Interacts with: EVERYTHING. If the primary queue is down, do poison
#     messages (Vector 2) still reach a queue to dead-letter from? Does
#     the fallback queue inherit the same MaxDeliveryCount=3 DLQ behaviour?
#     (Answer: yes — orders-fallback was configured with matching settings
#     in Post 37 specifically so DLQ semantics stay consistent.)
#
# ── WHAT "GRACEFUL DEGRADATION" MEANS HERE ────────────────────────────────
#
# The system passes this capstone if, by the end of Act 5's reconciliation
# query, every order sent during the chaos window is accounted for in
# exactly one outcome bucket:
#   - processed        (succeeded, possibly after Polly retry)
#   - dead-lettered     (poison messages — correctly rejected, not lost)
#   - fallback-routed   (during SB outage — correctly rerouted, not lost)
#
# FAILURE would look like: total accounted orders < total orders sent.
# That gap is silent data loss — the most dangerous outcome, because
# nothing throws an exception or fires an alert. It just... vanishes.
#
# ── ORDERING MATTERS: WHY VECTOR 4 STARTS AT T+5s, NOT T+0s ──────────────
#
# The chaos script deliberately waits 5 seconds before stopping Service Bus.
# This lets the load test (Vector 1) establish steady-state throughput first.
# Starting the outage at T+0 would conflate "system never reached steady
# state" with "system degraded under compound failure" — two different
# findings that need to stay distinguishable in the post-mortem.
#
# ── PRODUCTION CHAOS ENGINEERING TOOLS (BEYOND THIS SCRIPT) ──────────────
#
# This capstone hand-rolls chaos injection with docker stop/start and
# existing test endpoints — appropriate for a local learning environment.
# Production chaos engineering typically uses purpose-built tools:
#
#   Azure Chaos Studio — managed chaos experiments for Azure resources
#     (VM shutdown, network latency injection, AKS pod kill) with built-in
#     safety controls (auto-abort on SLA breach).
#
#   Chaos Mesh (Kubernetes-native) — for AKS environments, injects pod
#     kills, network partition, and I/O chaos directly into the cluster
#     via CRDs (Custom Resource Definitions).
#
#   Simmy (.NET, from the Polly team) — chaos injection AT THE POLLY
#     PIPELINE LEVEL. Rather than stopping real infrastructure, Simmy
#     injects synthetic faults (latency, exceptions, fault results)
#     directly into a resilience pipeline for unit/integration testing.
#     Recommended next step after this capstone: replace docker stop/start
#     with a Simmy chaos policy for faster, more targeted fault injection
#     in CI (Post 30's pr-checks.yml could run compound chaos on every PR).
#
# ── RUNBOOK CHECKLIST DERIVED FROM THIS CAPSTONE ─────────────────────────
#
# When investigating a real compound-failure incident:
#   1. Pull the ADX reconciliation query (Act 5) for the incident window
#   2. Confirm processed + dead-lettered + fallback-routed == total sent
#   3. If the totals don't reconcile: check for silent message loss in
#      the gap between Service Bus outage detection and Fallback activation
#      (the ~100ms window Post 37 documented as the application-level RTO)
#   4. Cross-reference with the distributed trace correlation (Post 39) —
#      any order whose TraceId shows only a CreateOrder span with no
#      PublishOrderEvent child indicates the message was lost before
#      even reaching the resilience layer
