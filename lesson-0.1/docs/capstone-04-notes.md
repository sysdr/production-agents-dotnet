# Capstone 04 — OTel Sampling: Configuration Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 36
#
# ── HEAD-BASED vs TAIL-BASED SAMPLING ────────────────────────────────────
#
# Head-based (probabilistic_sampler processor):
#   Decision made at REQUEST START — before the outcome is known.
#   Fast. Low memory. Simple configuration.
#   Problem: samples errors at the same rate as successes.
#   With 10% head sampling: you lose 90% of your error traces.
#   Never use head sampling as your only strategy in production.
#
# Tail-based (tail_sampling processor — this post):
#   Decision made at REQUEST END — after full trace is collected.
#   Requires buffering all spans for decision_wait duration.
#   Memory cost: num_traces × avg_spans_per_trace × avg_span_size
#   At 200 RPS × 10s × 5 spans × 1KB = ~10MB buffer (acceptable).
#   Allows policy-based decisions: always keep errors, sample successes.
#
# ── TUNING THE THREE POLICIES ─────────────────────────────────────────────
#
# Policy 1: keep-all-errors (status_code: [ERROR])
#   Never change this. Production post-mortems require 100% error trace fidelity.
#   A sampled error trace is worthless for debugging — the 5% you dropped
#   might be the ones showing the actual root cause.
#
# Policy 2: keep-slow-traces (latency: threshold_ms: 500)
#   Tune based on your p99 SLA:
#     SLA p99 = 200ms → set threshold_ms: 200 (keep all slow outliers)
#     SLA p99 = 1000ms → set threshold_ms: 1000 (less aggressive)
#   The Post 27 load test showed p99 = 812ms at 200 RPS → 500ms is a good threshold.
#
# Policy 3: sample-successful (probabilistic: sampling_percentage: 10)
#   Tune based on analytics needs:
#     sampling_percentage: 10  → p50/p99 estimates ±5% accuracy (good for 100+ spans)
#     sampling_percentage: 5   → p99 estimates may be noisy at low RPS windows
#     sampling_percentage: 20  → better accuracy, higher cost
#   Rule of thumb: at 200 RPS, 10% gives ~20 RPS of sampled traces.
#   That's 1200 traces per minute — enough for reliable p99 estimation.
#
# ── DO NOT SAMPLE THESE SIGNALS ──────────────────────────────────────────
#
# Logs (log records):
#   Warning and Error logs should NEVER be sampled.
#   Info logs are already low-volume in production (structured logging filters them).
#   The logs pipeline in Post 36 has NO sampling processor.
#
# Metrics (counters, histograms):
#   OTel metrics are already pre-aggregated by the SDK before export.
#   A counter value is the total count, not individual events.
#   Sampling metrics would reduce the count arbitrarily — never correct.
#   The metrics pipeline in Post 36 has NO sampling processor.
#
# Business metrics from Post 35:
#   order.value.usd histogram is already aggregated to p50/p99 before export.
#   tenant.order.count is a sum — sampling would give wrong billing numbers.
#   NEVER sample metrics.
#
# ── ADAPTIVE SAMPLING (advanced) ─────────────────────────────────────────
#
# Azure Application Insights Adaptive Sampling automatically adjusts the
# sampling rate to maintain a target volume (default: 5 items/second).
# It is equivalent to adding a rate_limiting policy to tail_sampling:
#
#   - name: rate-limit
#     type: rate_limiting
#     rate_limiting:
#       spans_per_second: 100   # adjust to hit your ADX budget
#
# This is more robust than a fixed 10% — at low traffic, everything is kept;
# at high traffic, it throttles to the budget limit.
# Add this policy AFTER the error and slow policies to preserve their priority.
#
# ── MEMORY SIZING FOR tail_sampling ──────────────────────────────────────
#
# tail_sampling buffers complete traces in memory until decision_wait expires.
# Memory estimate:
#   num_traces × avg_spans_per_trace × avg_span_size_bytes
#   10000 × 5 × 1024 = 51MB
#
# At 200 RPS: 10s decision window → 2000 concurrent traces maximum.
# num_traces: 10000 is 5× headroom — safe for bursts.
# If OTel Collector OOM-kills: reduce num_traces or reduce decision_wait.
#
# ── SAMPLING AND DISTRIBUTED TRACING ─────────────────────────────────────
#
# When a trace is sampled, ALL spans in that trace are sampled together.
# tail_sampling evaluates at the TRACE level, not the span level.
# This means:
#   - If /orders/create (parent span) is sampled → Service Bus send (child span) kept
#   - If /orders/create is dropped → Service Bus send span is also dropped
#   - A trace is NEVER partially sampled — all or nothing
#
# This is enforced by the W3C TraceContext propagation (trace-flags: 01 = sampled).
# OrderProcessor, UserService, etc. check the trace flag before recording spans.
