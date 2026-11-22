// src/ServiceDefaults/TraceContextPropagator.cs — POST 39
// W3C Trace Context propagation across Azure Service Bus messages.
//
// THE GAP THIS FIXES:
//   Service Bus (and Event Hubs) do NOT automatically propagate trace context
//   through AMQP the way HTTP does through headers. Without explicit injection,
//   OrderService.StartActivity("PublishOrderEvent") and
//   OrderProcessor.StartActivity("ProcessOrderMessage") each start their OWN
//   root trace — two disconnected TraceIds for what is conceptually one
//   end-to-end operation. ADX queries by TraceId only ever find one hop.
//
// THE FIX:
//   Inject the W3C traceparent header (https://www.w3.org/TR/trace-context/)
//   into ServiceBusMessage.ApplicationProperties on send.
//   Extract it on receive and pass it as the ParentContext to StartActivity.
//   This is the same mechanism ASP.NET Core uses automatically for HTTP —
//   Service Bus/Event Hubs require doing it by hand because there is no
//   built-in AMQP trace context propagator in the OTel .NET SDK (as of 1.8).
//
// BRIDGE FROM FAMILIAR:
//   WCF had "message correlation" via WS-Addressing MessageID headers —
//   manually stitched together in diagnostic tools. traceparent is the modern,
//   standardised equivalent: a single well-known header string that carries
//   TraceId + SpanId + trace flags across any transport, not just HTTP.
//
// WIRE FORMAT (W3C traceparent):
//   version-traceId-parentId-traceFlags
//   00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
//    │  └─ 32 hex chars (TraceId)      └─ 16 hex chars   └─ sampled flag
//    └─ spec version (always "00" today)

using System.Diagnostics;

namespace ServiceDefaults;

public static class TraceContextPropagator
{
    // POST 39: Application property key for the W3C traceparent header.
    // Using the standard header name (lowercase) makes this recognisable
    // to any tooling that already understands W3C Trace Context.
    public const string TraceParentKey = "traceparent";
    public const string TraceStateKey  = "tracestate";

    /// <summary>
    /// POST 39: Call this BEFORE sending a ServiceBusMessage.
    /// Injects the current Activity's trace context into ApplicationProperties
    /// so the consumer can reconstruct the parent-child span relationship.
    /// </summary>
    public static void InjectTraceContext(
        this IDictionary<string, object> applicationProperties,
        Activity? activity)
    {
        if (activity is null) return;

        // POST 39: Activity.Id is already formatted as a W3C traceparent string
        // when the Activity uses ActivityIdFormat.W3C (the .NET default since .NET 5+).
        applicationProperties[TraceParentKey] = activity.Id ?? string.Empty;

        // POST 39: TraceStateString carries vendor-specific trace state (optional).
        // Most systems in this series don't use it, but propagating it is free
        // and future-proofs against adding a vendor (e.g. Application Insights
        // sampling decisions encoded in tracestate).
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            applicationProperties[TraceStateKey] = activity.TraceStateString;
    }

    /// <summary>
    /// POST 39: Call this when starting an Activity for a RECEIVED message.
    /// Parses the traceparent from ApplicationProperties and returns an
    /// ActivityContext that StartActivity can use as the parent —
    /// this is what links the consumer span to the producer's trace.
    /// </summary>
    public static ActivityContext ExtractTraceContext(
        IReadOnlyDictionary<string, object> applicationProperties)
        => ExtractTraceContextCore(
            applicationProperties.TryGetValue,
            applicationProperties.TryGetValue);

    // Event Hubs EventData.Properties is IDictionary (not IReadOnlyDictionary).
    public static ActivityContext ExtractTraceContext(
        IDictionary<string, object> applicationProperties)
        => ExtractTraceContextCore(
            applicationProperties.TryGetValue,
            applicationProperties.TryGetValue);

    private static ActivityContext ExtractTraceContextCore(
        TryGetValue tryGetParent,
        TryGetValue tryGetState)
    {
        if (!tryGetParent(TraceParentKey, out var raw) ||
            raw is not string traceParent ||
            string.IsNullOrEmpty(traceParent))
        {
            // POST 39: No traceparent found — this is either a v1-era message
            // (produced before this post) or a bug in the sender. Returning
            // default(ActivityContext) means StartActivity creates a NEW root
            // trace rather than throwing. Forward-compatible: old messages
            // still process correctly, just without upstream correlation.
            return default;
        }

        // POST 39: ActivityContext.TryParse understands the W3C traceparent format
        // natively — no manual string splitting required.
        var traceState = tryGetState(TraceStateKey, out var ts)
            ? ts as string
            : null;

        return ActivityContext.TryParse(traceParent, traceState, out var context)
            ? context
            : default;
    }

    private delegate bool TryGetValue(string key, out object value);
}
