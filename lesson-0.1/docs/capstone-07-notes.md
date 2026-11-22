# Capstone 07 — Distributed Trace Correlation: Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 39
#
# ── WHY SERVICE BUS / EVENT HUBS DON'T AUTO-PROPAGATE TRACE CONTEXT ──────
#
# HTTP has a standard mechanism: the OTel ASP.NET Core + HttpClient
# instrumentation automatically reads/writes the "traceparent" HTTP header.
# Every HTTP call in this series (JWT validation, Dapr service invocation)
# gets automatic trace correlation for free.
#
# AMQP (the protocol under Service Bus and Event Hubs) has no equivalent
# built into the .NET OTel SDK as of version 1.8. The Azure.Messaging.ServiceBus
# and Azure.Messaging.EventHubs SDKs do NOT automatically inject or extract
# W3C trace context. This is a known gap — see the OTel .NET SDK contrib
# repository for the (currently incomplete) AMQP instrumentation proposal.
#
# Post 39 closes this gap manually using the same W3C Trace Context spec
# that HTTP uses under the hood — just applied by hand to ApplicationProperties
# (Service Bus) and EventData.Properties (Event Hubs).
#
# ── THE FOUR-HOP TRACE ────────────────────────────────────────────────────
#
#   Hop 1: CreateOrder (OrderService, HTTP request handler)
#     └─ Hop 2: PublishOrderEvent (OrderService, publish to SB + EH)
#          ├─ Hop 3: ProcessOrderMessage (OrderProcessor, SB consumer)
#          └─ Hop 4: ProcessOrderEvent   (OrderEventProcessor, EH consumer)
#
# Hops 3 and 4 are SIBLINGS — both children of Hop 2 (PublishOrderEvent),
# not of each other. This correctly models the fan-out: one publish
# operation, two independent downstream consumers on different transports.
#
# ── W3C TRACEPARENT FORMAT ───────────────────────────────────────────────
#
#   00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
#   │  │                                │                │
#   │  │                                │                └─ flags (01 = sampled)
#   │  │                                └─ parent-id (16 hex = 8 bytes, the SpanId)
#   │  └─ trace-id (32 hex = 16 bytes, the TraceId)
#   └─ version (always "00" in the current spec)
#
# .NET's Activity.Id is already formatted this way when
# Activity.DefaultIdFormat == ActivityIdFormat.W3C (the default since .NET 5).
# This is why InjectTraceContext just copies activity.Id directly — no manual
# string formatting needed.
#
# ── WHY ActivityKind.Consumer MATTERS ────────────────────────────────────
#
# StartActivity(name, ActivityKind.Consumer, parentContext) sets the span's
# "kind" attribute to Consumer. This is a W3C/OTel semantic convention that
# tells trace-viewing tools (Jaeger, Application Insights, ADX) that this
# span represents message consumption, not an HTTP server request or an
# outbound RPC call. Setting the correct SpanKind:
#   - Improves trace visualisation (consumer spans render differently)
#   - Enables OTel semantic convention queries: "show me all Consumer spans
#     with duration > 1s" without needing to filter by SpanName string match
#
# The producer side should symmetrically use ActivityKind.Producer, though
# this post keeps CreateOrder/PublishOrderEvent as the default Internal kind
# for simplicity — production code should set Producer explicitly on the
# publish spans.
#
# ── WHAT HAPPENS TO OLD MESSAGES (NO traceparent) ────────────────────────
#
# ExtractTraceContext returns default(ActivityContext) when traceparent is
# absent from ApplicationProperties/Properties. StartActivity with a default
# ActivityContext parent creates a NEW ROOT trace — the same behaviour as
# calling StartActivity with no parent argument at all.
#
# This means: messages produced before Post 39 (no traceparent injected)
# still process correctly. They just don't correlate back to their producer
# span — which is exactly what forward compatibility requires (Post 38's
# nullable-field principle applied to trace context, not data fields).
#
# ── VALIDATING IN PRODUCTION ──────────────────────────────────────────────
#
# The ADX Query 3 pattern (OrderId → TraceId → full trace, one query) is the
# production debugging workflow: a customer reports order #12345 was slow.
# You paste the OrderId into the query, get the TraceId, see all 4 spans
# with their durations, and immediately identify which hop was slow —
# without grepping through service-by-service logs manually.
#
# ── COMMON MISTAKE: FORGETTING ActivityKind on StartActivity OVERLOAD ────
#
# StartActivity has multiple overloads:
#   StartActivity(string name)                                    — no parent
#   StartActivity(string name, ActivityKind kind)                  — no parent
#   StartActivity(string name, ActivityKind kind, ActivityContext) — WITH parent ← use this one
#
# Calling StartActivity(name) after extracting parentContext does NOTHING —
# the extracted context is silently discarded if you use the wrong overload.
# Always use the 3-argument overload when linking to a parent context.
