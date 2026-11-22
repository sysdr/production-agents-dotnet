# Sentinel Detection Rules — KQL Reference
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# Post 19: KQL advanced — these rules run in Microsoft Sentinel (SIEM).
# They also work in Azure Monitor Log Analytics connected to Sentinel.
# The same KQL operators as App Insights and ADX; the table names differ.
#
# HOW SENTINEL RULES WORK
# ────────────────────────
# An analytics rule in Sentinel:
#   1. Runs a KQL query on a schedule (e.g., every 5 minutes)
#   2. If the query returns any rows → creates an Incident
#   3. The Incident appears in the Sentinel Incidents queue
#   4. Analysts triage the Incident (investigate, dismiss, close)
#
# The KQL query IS the detection logic. There is no separate rule engine.
# This is the same KQL you write in App Insights Logs, just against
# Sentinel's tables (SecurityEvent, SigninLogs, AzureActivity, etc.)
#
# App Insights equivalent
# ───────────────────────
# Post 19 maps each Sentinel rule to an App Insights equivalent
# so you can test the pattern against real data from the series.
# See the "App Insights equivalent" section under each rule.

---

## Rule 1 — Brute force login detection

### Sentinel version (runs against SigninLogs)
```kusto
// Detect: ≥10 failed sign-ins from the same IP in 5 minutes
// Alert if: any IP exceeds the threshold
let threshold  = 10;
let timeWindow = ago(5m);
SigninLogs
| where TimeGenerated > timeWindow
| where ResultType != "0"                      // non-zero = failure
| summarize
    FailCount  = count(),
    UserCount  = dcount(UserPrincipalName)
  by IPAddress, bin(TimeGenerated, 5m)
| where FailCount >= threshold
| project TimeGenerated, IPAddress, FailCount, UserCount
| order by FailCount desc
```

### App Insights equivalent (runs against App Insights traces)
```kusto
// Same pattern: detect repeated login failures from the same client
let threshold  = 3;
let timeWindow = ago(30m);
requests
| where timestamp > timeWindow
| where name has "users/login"
| where success == false
| summarize
    FailCount = count(),
    UserCount = dcount(tostring(customDimensions.UserId))
  by client_IP, bin(timestamp, 5m)
| where FailCount >= threshold
| project timestamp, client_IP, FailCount, UserCount
| order by FailCount desc
```

_Note: `client_IP` is populated automatically by the App Insights SDK from the incoming HTTP request IP. `UserId` is set by `SetTag(TelemetryConstants.AttrUserId)` from the series (Post 3)._

---

## Rule 2 — Anomalous order volume (business security)

### ADX / Sentinel version (uses make-series + series_decompose_anomalies)
```kusto
// Detect: statistically anomalous order volume per tenant
// Uses machine-learning anomaly detection built into KQL
let timeRange = ago(7d);
let binSize   = 1h;
requests
| where timestamp > timeRange
| where name has "orders/create"
| extend TenantId = tostring(customDimensions.TenantId)
| make-series OrderCount = count()
    on timestamp
    from timeRange to now()
    step binSize
    by TenantId
| extend (anomalies, score, baseline) = series_decompose_anomalies(OrderCount, 2.0)
| mv-expand timestamp, OrderCount, anomalies, score, baseline
| where toint(anomalies) != 0
| project timestamp, TenantId,
          OrderCount = toint(OrderCount),
          AnomalyScore = todouble(score),
          Baseline = toint(baseline)
| order by AnomalyScore desc
```

_`series_decompose_anomalies(series, threshold)` returns anomaly scores using a machine-learning decomposition. `threshold = 2.0` means: flag points more than 2 standard deviations from the baseline. Lower threshold = more sensitive._

---

## Rule 3 — DLQ accumulation alert

### App Insights / Azure Monitor version
```kusto
// Alert if DLQ exceeds threshold within any 5-minute window
// This is the alert missing from War Story #2
let threshold  = 0;     // alert on ANY DLQ message (post-mortem: threshold was effectively ∞)
let timeWindow = ago(30m);
traces
| where timestamp > timeWindow
| where message has "DLQ non-empty"
| extend DeadLetterCount = toint(customDimensions.DeadLetterCount)
| summarize
    MaxDLQ  = max(DeadLetterCount),
    Events  = count()
  by bin(timestamp, 5m)
| where MaxDLQ > threshold
| project timestamp, MaxDLQ, Events
| order by timestamp desc
```

_Create as an Azure Monitor Log Alert:
Run this query every 5 minutes. Alert if result count > 0. Severity: 2 (Warning)._

---

## Rule 4 — Elevated DeliveryCount (retry storm detection)

### App Insights version
```kusto
// Detect messages requiring >1 delivery attempt (Polly retries exhausted)
// High DeliveryCount indicates downstream service instability
let timeWindow = ago(1h);
traces
| where timestamp > timeWindow
| where message has "Processing order"
| extend DC = toint(customDimensions.DC)
| where DC > 1
| summarize
    Count     = count(),
    MaxDC     = max(DC),
    Tenants   = make_set(tostring(customDimensions.TenantId), 10)
  by bin(timestamp, 5m)
| where Count > 3
| project timestamp, Count, MaxDC, Tenants
| order by Count desc
```

---

## Rule 5 — Polly circuit breaker proximity alert

### App Insights version
```kusto
// Detect: ≥2 Polly retries per message in a 5-minute window
// Indicates we are approaching DeliveryCount exhaustion
let timeWindow = ago(1h);
let retryThreshold = 5;  // total retry events — 1 per Polly attempt
traces
| where timestamp > timeWindow
| where message has "Retrying order processing"
| summarize
    RetryCount  = count(),
    OrderCount  = dcount(tostring(customDimensions.OrderId))
  by bin(timestamp, 5m)
| where RetryCount >= retryThreshold
| project timestamp, RetryCount, OrderCount
| order by RetryCount desc
```

---

## KQL anomaly detection operators (ADX + App Insights)

```kusto
// make-series — create a time series for use with series functions
make-series Count = count()
    on timestamp
    from ago(7d) to now()
    step 1h
    by TenantId

// series_decompose_anomalies — ML-based anomaly detection
// Returns three parallel arrays: anomalies (-1/0/1), scores, baseline
| extend (anomalies, score, baseline) = series_decompose_anomalies(Count, 2.0)

// mv-expand — expand array columns into individual rows
// Required after make-series + series functions to get one row per time bucket
| mv-expand timestamp, Count, anomalies, score, baseline

// series_stats — basic statistics on a series column
| extend stats = series_stats(Count)
// Returns: min, max, mean, stdev, variance

// series_periods_detect — find repeating periods in a time series
| extend (periods, scores) = series_periods_detect(Count, 1, 48, 2)
// Args: series, min_period, max_period, num_periods
// Returns periods in number of bins (e.g., 24 bins × 1h = 24h period)
```

---

## Cross-workspace queries

```kusto
// Query multiple Log Analytics workspaces from a single Sentinel workspace
// workspace() references another workspace in the same Azure tenant
union
    requests,                                    // current workspace
    workspace("workspace-prod-east").requests,   // another region
    workspace("workspace-prod-west").requests
| where timestamp > ago(1h)
| summarize Count = count() by cloud_RoleName, bin(timestamp, 10m)
| order by timestamp desc

// Cross-cluster (ADX)
// cluster() references another ADX cluster
union
    cluster("adx-cluster-1").database("telemetry").OrderEvents,
    cluster("adx-cluster-2").database("telemetry").OrderEvents
| where ingestion_time() > ago(1h)
| summarize Count = count() by TenantId
```

---

## Performance notes

- **`make-series` requires a time range**: always specify `from` and `to` explicitly. Without them, the range defaults to the data extent, which varies run to run.
- **`mv-expand` creates one row per array element**: a `make-series` result with N time bins × M tenants produces M rows before `mv-expand` and M×N rows after. Filter with `where` BEFORE `mv-expand` to limit M; filter AFTER to limit N.
- **`series_decompose_anomalies` needs at least 4 data points**: with less than 4 bins, it returns null. For 5-minute bins, `ago(30m)` gives exactly 6 bins — the minimum for meaningful anomaly detection.
- **Cross-workspace queries are slower**: each referenced workspace adds a round-trip. Apply as many `where` clauses as possible before `union` to reduce data transferred.
