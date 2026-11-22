// src/OrderService/BusinessMetrics.cs — POST 35
// Custom business metrics for the order pipeline.
//
// WHAT THIS FILE ADDS:
//   Two new OTel metrics that were previously an observability GAP:
//   1. order.value.usd  — histogram of dollar values per order
//   2. tenant.order.count — counter of orders created per tenant
//
// WHY THESE WERE MISSING:
//   OTel captures request latency (spans) and queue depth (QueueDepthMonitor, Post 10)
//   automatically. Business-domain values — dollar amounts, order counts for billing —
//   require explicit instrumentation. They are silent absent, not missing-with-error.
//   Running the ADX query "show me total revenue by tenant last 30 days" returns
//   0 rows, not an error, until this file exists and is wired into the pipeline.
//
// BRIDGE FROM FAMILIAR:
//   Windows Performance Counters (PerfMon) required creating an InstallUtil-registered
//   PerformanceCounter in a separate setup step. .NET OTel Meter is code-first —
//   create a Meter, create instruments, record values. The SDK handles registration,
//   aggregation, and export.
//
// INSTRUMENT SELECTION GUIDE:
//   Counter<T>    — values that only go up (orders created, errors, retries)
//                   KQL: OTelMetrics | summarize sum(MetricValue) by TenantId
//   Histogram<T>  — distribution of values (latency, order dollar amounts)
//                   KQL: OTelMetrics | summarize percentile(MetricValue, 99)
//   Gauge<T>      — current point-in-time value (queue depth, active connections)
//                   KQL: OTelMetrics | summarize arg_max(Timestamp, MetricValue)
//   UpDownCounter<T> — values that rise and fall (concurrent requests, cache entries)

using System.Diagnostics.Metrics;

namespace OrderService;

/// <summary>
/// Business-domain OTel metrics for the order pipeline.
/// POST 35: These metrics fill the observability gap — domain values not
/// captured by infrastructure instrumentation (spans, queue depth, etc.).
/// </summary>
public static class BusinessMetrics
{
    // POST 35: One Meter per bounded context. The meter name becomes the
    // "instrumentation scope" visible in OTel Collector and ADX Attributes.
    // Use a dotted hierarchy matching the namespace: "Orders.Business".
    public static readonly Meter Meter = new("Orders.Business", "1.0");

    // POST 35: order.value.usd — Histogram records the distribution of order values.
    // Use Histogram (not Counter) when the statistical distribution matters:
    //   p50 order value tells you the median order size.
    //   p99 order value flags unusually large orders for fraud review.
    //   A Counter would only give you the total — you'd lose the distribution.
    //
    // Unit: "USD" — the UCUM unit string for US dollars.
    // ADX column: OTelMetrics.MetricValue (the recorded amount).
    // ADX query example:
    //   OTelMetrics | where MetricName == "order.value.usd"
    //   | summarize p50=percentile(MetricValue, 50), p99=percentile(MetricValue, 99)
    //               by TenantId
    public static readonly Histogram<double> OrderValue =
        Meter.CreateHistogram<double>(
            name:        "order.value.usd",
            unit:        "USD",
            description: "Dollar value of each order at time of creation");

    // POST 35: tenant.order.count — Counter tracks total orders per tenant.
    // Use Counter (not Histogram) when you only need the running total:
    //   billing systems need count-per-tenant, not value distribution.
    //   A Counter aggregates correctly in ADX: sum(MetricValue) = total orders.
    //
    // ADX query example:
    //   OTelMetrics | where MetricName == "tenant.order.count"
    //   | where Timestamp > ago(1d)
    //   | summarize dailyOrders = sum(MetricValue) by TenantId
    //   | order by dailyOrders desc
    public static readonly Counter<long> TenantOrderCount =
        Meter.CreateCounter<long>(
            name:        "tenant.order.count",
            description: "Total orders created, partitioned by tenant");
}
