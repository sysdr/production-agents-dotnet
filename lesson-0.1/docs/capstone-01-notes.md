# Capstone 01 — Multi-Tenant Order Burst: Incident Playbook
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 33
#
# ── SCENARIO OVERVIEW ───────────────────────────────────────────────────
#
# What happens:
#   Two tenants simultaneously spike from 50 RPS to 200 RPS.
#   OrderProcessor queue depth rises faster than 1 pod can drain it.
#   On AKS: KEDA scales from 1 → 10 replicas.
#   5 poison messages are injected during the burst.
#   Each poison message fails 3× (MaxDeliveryCount:3) then enters the DLQ.
#   The QueueDepthMonitor gauge (Post 10) emits deadletter_messages > 0.
#   The SRE runbook DLQ alert (Post 23) fires in the Azure dashboard.
#   ADX KQL query proves the p99 impact over the full 24h window.
#
# ── WHAT EACH COMPONENT CONTRIBUTES ─────────────────────────────────────
#
# Phase 1 (Posts 1-6): OTel spans capture every request. The Collector logs
#   show LogRecord and Span entries for both tenant-01 and tenant-02.
#
# Phase 2 (Posts 7-9): Service Bus emulator receives burst traffic.
#   MaxDeliveryCount:3 ensures poison messages DLQ after exactly 3 attempts.
#   AutoCompleteMessages=false ensures only successfully processed messages
#   are completed — crashed processing retries automatically.
#
# Phase 2 (Post 10): QueueDepthMonitor polls every 30s.
#   At peak burst, sb.orders.active_messages rises to 100-300+.
#   After DLQ injection, sb.orders.deadletter_messages rises to 5.
#
# Phase 2 (Posts 12-13): Every order is also published to Event Hubs.
#   TenantId is the partition key — tenant-01 and tenant-02 get separate
#   partitions, so their events don't interfere.
#
# Phase 4 (Posts 20-21): Every order is authenticated via JWT.
#   TenantConfigStore uses IMemoryCache (Post 28) — the cache absorbs
#   200 Table Storage lookups/s at peak, limiting p99 impact.
#
# Phase 4 (Post 23): The SRE dashboard's DLQ panel shows the deadletter
#   count climbing. The runbook says: "if DLQ > 0, check QueueDepthMonitor,
#   diagnose the failed messages, replay or discard."
#
# Phase 6 (Post 27): NBomber produces two HTML reports — baseline and burst.
#   Side-by-side comparison shows p99 jump between the two windows.
#
# Phase 9 (Post 31): On AKS, the KEDA ScaledObject fires.
#   pollingInterval:15s → within 15s of messageCount>5, a second pod starts.
#   cooldownPeriod:300s → pods stay warm 5 minutes after queue drains.
#
# Phase 9 (Post 32): ADX receives all spans via the dual-export pipeline.
#   The forensics KQL query shows BASELINE vs BURST p99 side by side.
#   This is the evidence used in a post-mortem to prove the burst caused
#   the latency spike — not a code deployment, not a dependency failure.
#
# ── INCIDENT RESPONSE SEQUENCE ──────────────────────────────────────────
#
# T+0s   Burst begins. NBomber ramps to 200 RPS.
# T+15s  KEDA polls. Queue depth > 5. Second OrderProcessor pod scheduled.
# T+30s  Second pod ready. Queue starts draining.
# T+45s  Burst continues. Queue depth stabilises at ~100 messages.
# T+60s  Burst ends. Queue depth begins falling.
# T+75s  Poison messages have exhausted MaxDeliveryCount:3. DLQ fills.
# T+90s  QueueDepthMonitor emits sb.orders.deadletter_messages = 5.
# T+120s SRE dashboard DLQ panel turns red. Runbook alert fires.
# T+180s Queue fully drained. KEDA starts cooldownPeriod (300s).
# T+480s cooldownPeriod ends. KEDA scales back to minReplicaCount:1.
#
# ── DLQ FORENSICS (Post 9 runbook) ──────────────────────────────────────
#
# When the DLQ alert fires:
#   1. Check message body: what was in the poison order?
#      az servicebus queue message peek --queue-name orders
#        --namespace-name YOUR_SB_NAMESPACE --resource-group YOUR_RG
#        --subqueue deadLetterQueue
#
#   2. Check DeadLetterReason in the message properties:
#      MaxDeliveryCountExceeded — message failed 3 times, no more retries
#      MessageLockLostException — processing took too long (lock expired)
#
#   3. Decision:
#      a) Replay: fix the underlying bug, re-enqueue from DLQ
#      b) Discard: message was malformed, remove from DLQ
#      c) Alert on content: if PII or payment data — escalate to security
#
# ── CAPSTONE SCRIPT PARAMETERS ──────────────────────────────────────────
#
#   -BaselineRps  (default 50)   RPS for the pre-burst baseline
#   -BurstRps     (default 200)  RPS for the burst window
#   -BaselineSec  (default 30)   seconds for baseline window
#   -BurstSec     (default 60)   seconds for burst window
#   -PoisonCount  (default 5)    number of poison messages to inject
#   -SkipADX                     skip ADX forensics section
#
# On slower developer machines, reduce burst parameters:
#   .\docs\capstone-01.ps1 -BurstRps 100 -BurstSec 30 -PoisonCount 2
