# KQL Query Reference
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# These queries run in Application Insights > Logs blade (KQL).
# They also work in Azure Monitor Log Analytics with minor table-name adjustments.
# Post 18 covers the KQL fundamentals — this file is the production reference.
#
# HOW TO USE
# 1. Open Application Insights in Azure Portal (or local emulator — see Post 15)
# 2. Click "Logs" in the left navigation
# 3. Paste a query from this file
# 4. Click "Run"
#
# CORRELATION KEY
# Every record from one end-to-end request shares the same operation_Id (= OTel TraceId).
# Use operation_Id to join across all five tables.

---

## Table 1: requests — inbound HTTP calls to your services

### p99 latency by endpoint (last 24 hours)
```kusto
requests
| where timestamp > ago(24h)
| summarize p99 = percentile(duration, 99),
            p95 = percentile(duration, 95),
            p50 = percentile(duration, 50),
            Count = count()
  by name
| order by p99 desc
```

### Error rate by endpoint (last 1 hour)
```kusto
requests
| where timestamp > ago(1h)
| summarize Total = count(),
            Errors = countif(success == false)
  by name
| extend ErrorRate = round(100.0 * Errors / Total, 2)
| order by ErrorRate desc
```

### Slow orders — requests over 500ms to /orders/create
```kusto
requests
| where timestamp > ago(1h)
| where name has "orders/create"
| where duration > 500
| project timestamp, duration, id, operation_Id,
          TenantId = tostring(customDimensions.TenantId),
          OrderId  = tostring(customDimensions.OrderId)
| order by duration desc
```

---

## Table 2: dependencies — outbound calls (Service Bus, Event Hubs, HTTP)

### Dependency failure rate by target (last 1 hour)
```kusto
dependencies
| where timestamp > ago(1h)
| summarize Total  = count(),
            Errors = countif(success == false)
  by target, type
| extend FailureRate = round(100.0 * Errors / Total, 2)
| order by FailureRate desc
```

### Service Bus send latency (last 24 hours)
```kusto
dependencies
| where timestamp > ago(24h)
| where type == "Azure Service Bus"
| summarize p99 = percentile(duration, 99),
            Count = count()
  by target
| order by p99 desc
```

### Event Hubs publish failures
```kusto
dependencies
| where timestamp > ago(24h)
| where type == "Azure Event Hubs"
| where success == false
| project timestamp, target, name, resultCode, duration, operation_Id
| order by timestamp desc
```

---

## Table 3: exceptions — unhandled exceptions captured by App Insights

### Exception count by type (last 7 days)
```kusto
exceptions
| where timestamp > ago(7d)
| summarize Count = count() by type
| order by Count desc
| take 10
```

### Exceptions with full stack trace (last 1 hour)
```kusto
exceptions
| where timestamp > ago(1h)
| project timestamp, type, outerMessage, innermostMessage,
          operation_Id, cloud_RoleName
| order by timestamp desc
```

### Exception spike detection — more than 10 exceptions per minute
```kusto
exceptions
| where timestamp > ago(1h)
| summarize Count = count() by bin(timestamp, 1m)
| where Count > 10
| order by timestamp desc
```

---

## Table 4: traces — structured log records (LogInformation, LogWarning, LogError)

### Error and warning logs by service (last 1 hour)
```kusto
traces
| where timestamp > ago(1h)
| where severityLevel >= 2  // 2=Warning, 3=Error, 4=Critical
| summarize Count = count() by cloud_RoleName, severityLevel
| order by severityLevel desc, Count desc
```

### DLQ warning logs
```kusto
traces
| where timestamp > ago(24h)
| where message has "DLQ non-empty"
| project timestamp, message, cloud_RoleName,
          DeadLetterCount = tostring(customDimensions.DeadLetterCount),
          ActiveCount     = tostring(customDimensions.ActiveCount)
| order by timestamp desc
```

### Polly retry warnings (transient failure pattern)
```kusto
traces
| where timestamp > ago(1h)
| where message has "Retrying order processing"
| project timestamp, message, operation_Id,
          Attempt  = tostring(customDimensions.AttemptNumber),
          DelayMs  = tostring(customDimensions.DelayMs)
| order by timestamp desc
```

---

## Table 5: customEvents — custom business events

### Order creation rate by tenant (last 24 hours)
```kusto
customEvents
| where timestamp > ago(24h)
| where name has "order"
| summarize Count = count() by bin(timestamp, 1h),
            TenantId = tostring(customDimensions.TenantId)
| order by timestamp asc
| render timechart
```

### Orders processed vs failed (last 1 hour)
```kusto
customEvents
| where timestamp > ago(1h)
| summarize Count = count()
  by name, Status = tostring(customDimensions.Status)
| order by Count desc
```

---

## Cross-table correlation queries

### Full trace — all telemetry for one operation_Id
```kusto
// Replace with your actual operation_Id (= OTel TraceId from Aspire Dashboard)
let traceId = "YOUR_TRACE_ID_HERE";
union requests, dependencies, exceptions, traces
| where operation_Id == traceId
| project timestamp, itemType, name, duration, success, message
| order by timestamp asc
```

### Tenant health check — all errors for one tenant in last 1 hour
```kusto
let tenant = "tenant-03";
union requests, dependencies, exceptions, traces
| where timestamp > ago(1h)
| where tostring(customDimensions.TenantId) == tenant
| where (itemType == "exception")
      or (itemType == "request" and success == false)
      or (itemType == "trace" and severityLevel >= 2)
| project timestamp, itemType, name, message, operation_Id
| order by timestamp desc
```

### End-to-end order journey — from HTTP create to SB process to EH consume
```kusto
// Find orders that took more than 2 seconds end-to-end
requests
| where timestamp > ago(1h)
| where name has "orders/create"
| extend OrderId = tostring(customDimensions.OrderId)
| join kind=inner (
    traces
    | where message has "Order processed"
    | extend OrderId = tostring(customDimensions.OrderId)
    | project CompletedAt = timestamp, OrderId
) on OrderId
| extend EndToEndMs = datetime_diff('millisecond', CompletedAt, timestamp)
| where EndToEndMs > 2000
| project timestamp, OrderId, EndToEndMs,
          TenantId = tostring(customDimensions.TenantId), operation_Id
| order by EndToEndMs desc
```

---

## OTel → App Insights field mapping

| OTel concept          | App Insights field            | KQL table    |
|-----------------------|-------------------------------|--------------|
| TraceId               | operation_Id                  | all tables   |
| SpanId                | id (requests) / id (deps)     | requests, dependencies |
| ParentSpanId          | operation_ParentId            | all tables   |
| `SetTag("TenantId")`  | customDimensions["TenantId"]  | all tables   |
| `SetTag("OrderId")`   | customDimensions["OrderId"]   | all tables   |
| LogInformation()      | traces (severityLevel=1)      | traces       |
| LogWarning()          | traces (severityLevel=2)      | traces       |
| LogError()            | traces (severityLevel=3)      | traces       |
| Exception             | exceptions.innermostMessage   | exceptions   |
| HttpClient outbound   | dependencies (type="HTTP")    | dependencies |
| SB SendMessageAsync   | dependencies (type="Azure Service Bus") | dependencies |
