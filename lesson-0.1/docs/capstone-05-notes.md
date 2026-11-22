# Capstone 05 — Multi-Region Failover: Architecture Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 37
#
# ── PRODUCTION FAILOVER ARCHITECTURE ─────────────────────────────────────
#
# Locally (Post 37 simulation):
#   Primary:  orders queue on servicebus-emulator:5672
#   Fallback: orders-fallback queue on same emulator
#   Failover: Polly FallbackAction activates on any ServiceBusException
#
# Production (two Azure regions):
#   Primary:  orders queue — Service Bus Namespace in East US
#   Fallback: orders queue — Service Bus Namespace in West US
#   Failover: Same Polly pipeline, different connection string
#
# Configuration swap (appsettings.Production.json):
#   "ServiceBus__PrimaryConnectionString":  "<East US namespace connection string>"
#   "ServiceBus__FallbackConnectionString": "<West US namespace connection string>"
#
# ── POLLY STRATEGIES FOR SERVICE BUS FAILOVER ────────────────────────────
#
# FallbackAction (this post):
#   Triggers on first exception. No delay. Immediate reroute.
#   Best for: immediate failover to standby queue.
#   Risk: a transient network hiccup causes unnecessary fallback traffic.
#
# CircuitBreaker + FallbackAction (production recommendation):
#   CircuitBreaker counts consecutive failures.
#   After N failures, circuit OPENS — no more primary attempts.
#   After cooldown, circuit goes HALF-OPEN — one test request.
#   If test succeeds, circuit CLOSES — primary resumes.
#
#   pipeline = new ResiliencePipelineBuilder()
#     .AddCircuitBreaker(new CircuitBreakerStrategyOptions {
#         FailureRatio      = 0.5,     // 50% failure rate triggers open
#         SamplingDuration  = TimeSpan.FromSeconds(30),
#         MinimumThroughput = 10,      // need at least 10 requests in window
#         BreakDuration     = TimeSpan.FromSeconds(30),
#         ShouldHandle      = new PredicateBuilder().Handle<ServiceBusException>()
#     })
#     .AddFallback(...)
#     .Build();
#
# Retry + FallbackAction (not recommended for SB failover):
#   Retry 3× on the same primary before falling back.
#   Problem: each retry adds latency. At 200 RPS, 3 retries × 500ms timeout
#   = 1.5s added per failed order during outage. Queue builds up rapidly.
#   Only use retry for transient errors (timeout, rate limit) not outages.
#
# ── THREE CIRCUIT BREAKER STATES ─────────────────────────────────────────
#
# CLOSED (healthy normal state):
#   All requests go to primary. Failure counter tracks recent failures.
#   When failure ratio > threshold: transitions to OPEN.
#
# OPEN (outage detected):
#   All requests bypass primary and go to fallback immediately.
#   No primary attempts. No latency from failed primary calls.
#   After BreakDuration: transitions to HALF-OPEN.
#
# HALF-OPEN (testing recovery):
#   One test request goes to primary.
#   If primary succeeds: CLOSES (primary resumes, fallback stops).
#   If primary fails: OPENS again (fallback continues, BreakDuration resets).
#
# ── ORDERS-FALLBACK QUEUE CONFIGURATION ──────────────────────────────────
#
# The orders-fallback queue (added to servicebus-config.json in Post 37):
#   MaxDeliveryCount: 3          (same as primary — consistent DLQ behaviour)
#   RequiresDuplicateDetection: false  (no dedup — fallback may replay edge cases)
#
# OrderProcessor in production watches BOTH queues:
#   - Primary worker: ServiceBusProcessor on "orders"
#   - Fallback worker: ServiceBusProcessor on "orders-fallback"
#   Both workers run the same OrderProcessorWorker logic.
#   Post 37: OrderProcessor only watches "orders". The notes document
#   the production extension needed.
#
# ── RECOVERY TIME OBJECTIVE (RTO) ────────────────────────────────────────
#
# This post's implementation (FallbackAction only):
#   RTO = time of first ServiceBusException + Polly evaluation = < 100ms
#   Orders continue flowing with no client-visible error.
#
# Azure Service Bus Geo-Disaster Recovery (production):
#   Failover initiation: < 1 minute (automated or manual trigger)
#   DNS propagation:     < 5 minutes
#   Effective RTO:       ~5-10 minutes with automatic geo-failover
#
# With this Polly pattern, your application-level RTO (client sees no error)
# is < 100ms even during Azure's 5-minute infrastructure failover window.
# The Polly fallback bridges the gap.
#
# ── MONITORING DURING FAILOVER ────────────────────────────────────────────
#
# Signal 1: OTel log "ServiceBus.FallbackActivated"
#   KQL: OTelLogs | where Body contains "FallbackActivated" | count
#   Alert threshold: > 5 in 5 minutes → page on-call
#
# Signal 2: OTelTraces span tag "sb.route" = "fallback"
#   KQL: OTelTraces | where Attributes["sb.route"] == "fallback" | count
#   Alert threshold: any non-zero value during business hours
#
# Signal 3: orders-fallback queue depth rising
#   The QueueDepthMonitor (Post 10) should also watch "orders-fallback".
#   Add a second ObservableGauge for the fallback queue depth.
#   Alert threshold: orders-fallback depth > 0 → primary is degraded
