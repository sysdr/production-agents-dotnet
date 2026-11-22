# Azure Data Explorer (ADX) Notes — Post 32
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# ── ZERO-SUBSCRIPTION SETUP: ADX Free Cluster ─────────────────────────────
#
#   1. Go to https://dataexplorer.azure.com/freecluster
#   2. Sign in with a Microsoft account (no Azure subscription needed)
#   3. Note your cluster URI: https://<name>.eastus.kusto.windows.net
#   4. Your free cluster has 1 database pre-created — rename it to "telemetry":
#        .rename database <default_name> to telemetry
#
# ── TABLE CREATION (run in ADX Web UI → Query tab) ────────────────────────

# OTelMetrics table — receives all metric data points
# .create table OTelMetrics (
#     Timestamp:          datetime,
#     MetricName:         string,
#     MetricValue:        real,
#     MetricUnit:         string,
#     ServiceName:        string,
#     TenantId:           string,
#     Environment:        string,
#     Attributes:         dynamic
# )

# OTelLogs table — receives all log records
# .create table OTelLogs (
#     Timestamp:          datetime,
#     SeverityText:       string,
#     Body:               string,
#     TraceId:            string,
#     SpanId:             string,
#     ServiceName:        string,
#     TenantId:           string,
#     Environment:        string,
#     Attributes:         dynamic
# )

# OTelTraces table — receives all span records
# .create table OTelTraces (
#     Timestamp:          datetime,
#     TraceId:            string,
#     SpanId:             string,
#     ParentSpanId:       string,
#     SpanName:           string,
#     SpanKind:           string,
#     ServiceName:        string,
#     DurationMs:         real,
#     StatusCode:         string,
#     TenantId:           string,
#     Attributes:         dynamic
# )

# ── INGESTION MAPPINGS ──────────────────────────────────────────────────────
# The azuredataexplorer OTel exporter maps OTel fields automatically.
# Custom mappings only needed if you rename columns:

# .create table OTelMetrics ingestion json mapping 'OTelMetricsMapping' '['
#   {"column":"Timestamp",   "path":"$.Timestamp",   "datatype":"datetime"},'
#   {"column":"MetricName",  "path":"$.Name",         "datatype":"string"},'
#   {"column":"MetricValue", "path":"$.Sum",          "datatype":"real"},'
#   {"column":"ServiceName", "path":"$.Attributes.service\\.name", "datatype":"string"}'
# ']'

# ── USEFUL KQL QUERIES ──────────────────────────────────────────────────────

# 1. p99 latency per tenant over 24h (ADX version — compare to App Insights KQL in Post 18)
#
# OTelTraces
# | where Timestamp > ago(24h)
# | where SpanName == "POST /orders/create"
# | summarize p99 = percentile(DurationMs, 99) by TenantId, bin(Timestamp, 1h)
# | render timechart

# 2. Order creation rate per tenant (last 7 days)
#
# OTelMetrics
# | where Timestamp > ago(7d)
# | where MetricName == "orders.created"
# | summarize total = sum(MetricValue) by TenantId, bin(Timestamp, 1d)
# | render columnchart

# 3. Error rate by service (last 30 days) — capacity planning
#
# OTelLogs
# | where Timestamp > ago(30d)
# | where SeverityText in ("ERROR", "CRITICAL")
# | summarize errors = count() by ServiceName, bin(Timestamp, 1d)
# | join kind=inner (
#     OTelLogs
#     | where Timestamp > ago(30d)
#     | summarize total = count() by ServiceName, bin(Timestamp, 1d)
# ) on ServiceName, Timestamp
# | extend errorRate = todouble(errors) / todouble(total) * 100
# | render timechart

# 4. Tenant onboarded before a specific date — billing query
#
# OTelTraces
# | where Timestamp between (datetime(2025-01-01) .. datetime(2025-03-31))
# | where SpanName contains "TenantConfigStore"
# | summarize firstSeen = min(Timestamp) by TenantId
# | order by firstSeen asc

# ── KQL TRANSLATION TABLE: App Insights → ADX ─────────────────────────────
#
# App Insights KQL              ADX KQL equivalent
# ─────────────────────────────────────────────────────────────────────────
# requests                      OTelTraces | where SpanKind == "Server"
# dependencies                  OTelTraces | where SpanKind == "Client"
# traces                        OTelLogs
# customMetrics                 OTelMetrics
# customEvents                  OTelLogs | where Attributes has "event"
# operation_Id                  TraceId
# operation_ParentId            ParentSpanId
# timestamp                     Timestamp
# duration                      DurationMs
# resultCode                    StatusCode
# cloud_RoleName                ServiceName

# ── COST COMPARISON (illustrative) ─────────────────────────────────────────
#
# At 200 RPS for 30 days:
#   ~518M records/month (200 rps × 60s × 60m × 24h × 30d)
#
# App Insights:
#   Data ingestion: ~5 GB/month at $2.30/GB = ~$11.50
#   Retention beyond 90 days: $0.10/GB/month
#   Query: $5.00 per TB scanned
#
# ADX (free cluster — dev only):
#   Cost: $0 (capped at 100 GB storage, 1 vCore)
#
# ADX (Standard_D4s_v3, 2-node cluster — production):
#   Compute: ~$300/month
#   Storage: $0.023/GB compressed (typically 10:1 compression on OTel data)
#   Break-even vs App Insights at ~130 GB/month ingest or 6-month retention needs
