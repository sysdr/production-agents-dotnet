# Capstone 03 — Observability Gap Analysis: Playbook
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 35
#
# ── WHAT AN OBSERVABILITY GAP LOOKS LIKE ────────────────────────────────
#
# Scenario: product manager asks "what's our total revenue by tenant last month?"
# Engineer runs: OTelMetrics | where MetricName == "order.value.usd"
# Result: 0 rows returned.
#
# Two possible interpretations:
#   A) There were no orders last month.
#   B) Orders were created but order value was never instrumented.
#
# Without the metric, you cannot tell which one is true. This is the gap:
# a missing metric is indistinguishable from a zero-value metric.
#
# ── HOW TO SYSTEMATICALLY AUDIT FOR GAPS ─────────────────────────────────
#
# Step 1: List what SHOULD be measured
#   For each business action, ask: "What would a product manager or billing
#   system need to know about this event?"
#   For /orders/create:
#     - How many? (Counter)
#     - How much value? (Histogram)
#     - How long did it take? (Histogram — already captured by OTel spans)
#     - Which tenant? (Tag on all metrics above)
#
# Step 2: Check the OTel Collector logs
#   docker logs otel-collector 2>&1 | Select-String "MetricName"
#   Compare the list of MetricNames in logs against your "should be" list.
#
# Step 3: Check the ADX OTelMetrics table
#   OTelMetrics | distinct MetricName | order by MetricName asc
#   Missing from this list = observability gap.
#
# ── INSTRUMENT SELECTION GUIDE ────────────────────────────────────────────
#
# Counter<T>   — use when you only need the total (e.g. orders created, errors)
#   Meter.CreateCounter<long>("tenant.order.count")
#   Record with: counter.Add(1, tags)
#   ADX query:   | summarize total = sum(MetricValue)
#
# Histogram<T> — use when distribution matters (e.g. latency, order value)
#   Meter.CreateHistogram<double>("order.value.usd", "USD")
#   Record with: histogram.Record(value, tags)
#   ADX query:   | summarize p99 = percentile(MetricValue, 99)
#
# Gauge<T>     — use for point-in-time readings (e.g. queue depth, active users)
#   Prefer ObservableGauge for polling-based readings:
#   Meter.CreateObservableGauge("active.connections", () => GetActiveCount())
#   ADX query:   | summarize current = arg_max(Timestamp, MetricValue)
#
# UpDownCounter<T> — use for values that can decrease (e.g. pending orders)
#   Meter.CreateUpDownCounter<long>("orders.pending")
#   Record +1 when created, -1 when completed
#
# ── COMMON GAPS IN .NET DISTRIBUTED SYSTEMS ──────────────────────────────
#
# 1. Business value metrics (this post)
#    Gap: request latency is captured (spans), business amounts are not
#    Fix: Histogram<double> for dollar amounts, rates, sizes
#
# 2. Batch operation counts
#    Gap: individual requests are traced, bulk operations are not
#    Fix: Counter<long> for items processed in each batch
#
# 3. Cache effectiveness
#    Gap: cache exists but hit/miss ratio is unknown
#    Fix: Counter<long> "cache.hits" and "cache.misses" in TenantConfigStore
#
# 4. Retry frequency
#    Gap: Polly retry fires but count is not exported as a metric
#    Fix: Counter<long> "order.processing.retries" in OrderProcessorWorker
#
# 5. DLQ drain rate
#    Gap: DLQ depth is measured, but time-to-drain is not
#    Fix: Histogram<double> "dlq.message.age.seconds" when a DLQ message is replayed
#
# ── THE METER REGISTRATION TRAP ──────────────────────────────────────────
#
# The most common gap cause in .NET OTel:
#
#   // Creates the Histogram — but nobody is listening yet
#   public static readonly Histogram<double> OrderValue =
#       Meter.CreateHistogram<double>("order.value.usd");
#
#   // THIS IS REQUIRED in Program.cs:
#   builder.Services.AddOpenTelemetry()
#       .WithMetrics(m => m.AddMeter("Orders.Business"));
#
#   Without AddMeter(), the OTel SDK creates the instrument but no reader
#   subscribes to it. Measurements vanish. The Collector never sees them.
#   Zero rows in ADX. Indistinguishable from zero orders.
#
# Debug command — check if the meter is being collected:
#   docker logs otel-collector 2>&1 | Select-String "Orders.Business"
#   (should appear in metric export lines once orders are sent)
