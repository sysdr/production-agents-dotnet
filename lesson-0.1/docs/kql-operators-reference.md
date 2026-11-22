# KQL Operators Quick Reference
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# Post 18: KQL fundamentals — the operators used in docs/kql-queries.md
#
# These operators work in: Application Insights · Azure Monitor · ADX · Sentinel
# Same syntax across all four. Table names differ; operators do not.

---

## Core pipe operators

### where — filter rows
```kusto
| where timestamp > ago(1h)                     // time range
| where severityLevel >= 2                       // numeric comparison
| where name has "orders/create"                 // substring match
| where name contains "order"                    // case-insensitive contains
| where name startswith "POST"                   // prefix match
| where success == false                         // boolean
| where duration between (500 .. 2000)           // range
| where name in ("GET /health", "POST /orders")  // set membership
```

### project — select and rename columns
```kusto
| project timestamp, name, duration, success     // select columns
| project Endpoint = name, DurationMs = duration  // rename
| project-away timestamp, itemType               // exclude columns
| project-rename TenantId = customDimensions.TenantId  // rename in place
```

### extend — add computed columns
```kusto
| extend DurationSec = duration / 1000.0               // arithmetic
| extend TenantId = tostring(customDimensions.TenantId) // dynamic field cast
| extend IsSlowRequest = duration > 500                // boolean column
| extend Hour = bin(timestamp, 1h)                     // time bucket
```

### summarize — aggregate
```kusto
| summarize Count = count() by name                     // count per endpoint
| summarize p99 = percentile(duration, 99) by name      // p99 per endpoint
| summarize Count = count() by bin(timestamp, 1h)       // count per hour
| summarize
    p99   = percentile(duration, 99),
    p95   = percentile(duration, 95),
    Count = count()
  by name, bin(timestamp, 1h)                           // multi-aggregate, multi-group
```

### order by — sort results
```kusto
| order by timestamp desc                 // newest first
| order by p99 desc, Count asc           // multi-column sort
| top 10 by duration desc                // top N rows by column
```

### take / limit — cap result set
```kusto
| take 100      // return at most 100 rows (arbitrary order)
| limit 100     // alias for take
```

### join — combine tables
```kusto
requests
| where name has "orders"
| join kind=inner (
    traces
    | where message has "Order processed"
    | project operation_Id, ProcessedAt = timestamp
) on operation_Id
| project timestamp, name, ProcessedAt, duration
```

Join kinds: `inner` (both sides must match), `leftouter` (all left rows), `rightouter`, `fullouter`, `anti` (left rows with no match on right).

### union — combine rows from multiple tables
```kusto
union requests, dependencies, exceptions, traces
| where operation_Id == "abc123"
| order by timestamp asc
```

### render — visualise
```kusto
| render timechart                 // line chart with timestamp on X axis
| render barchart                  // bar chart
| render piechart                  // pie chart
| render table                     // data table (default)
| render columnchart               // column chart
```

---

## Time functions

```kusto
ago(1h)                    // 1 hour ago (from now)
ago(7d)                    // 7 days ago
bin(timestamp, 1h)         // round timestamp down to nearest hour
bin(timestamp, 1d)         // round to day
startofday(now())          // midnight today
startofweek(now())         // Sunday (or Monday depending on locale)
datetime_diff('minute', t2, t1)   // difference in minutes between two timestamps
format_datetime(timestamp, 'yyyy-MM-dd HH:mm')  // format timestamp
```

---

## String functions

```kusto
tostring(customDimensions.TenantId)     // cast dynamic to string
toint(customDimensions.DeliveryCount)   // cast to integer
toupper(name)                           // uppercase
tolower(cloud_RoleName)                 // lowercase
split(name, "/", 1)                     // split on delimiter, return index 1
extract("(tenant-[0-9]+)", 1, message) // regex extract
strcat("prefix-", tenantId)            // string concat
```

---

## Aggregation functions

```kusto
count()                    // count rows
dcount(tenantId)           // distinct count
sum(duration)              // sum
avg(duration)              // average
min(duration)              // minimum
max(duration)              // maximum
percentile(duration, 50)   // median
percentile(duration, 99)   // 99th percentile
percentiles(duration, 50, 90, 95, 99)  // multiple percentiles in one pass
countif(success == false)  // conditional count
sumif(duration, duration > 1000)  // conditional sum
make_list(name, 10)        // collect up to 10 values into an array
make_set(tenantId)         // collect distinct values into an array
```

---

## customDimensions access patterns

OTel `SetTag("TenantId", "tenant-03")` → `customDimensions["TenantId"]`

```kusto
// Always cast to string for comparison (customDimensions is dynamic)
| where tostring(customDimensions.TenantId) == "tenant-03"
| extend TenantId = tostring(customDimensions.TenantId)

// Cast to int for numeric operations
| extend DC = toint(customDimensions.DeliveryCount)
| where DC > 1

// Bracket syntax for keys with special characters
| where customDimensions["trace_id"] != ""

// Check if key exists
| where isnotempty(tostring(customDimensions.OrderId))
```

---

## Let statements — reusable query variables

```kusto
// Define variables and subqueries for reuse
let timeRange = ago(24h);
let slowThreshold = 500;
let slowEndpoints =
    requests
    | where timestamp > timeRange
    | where duration > slowThreshold
    | summarize Count = count() by name;
slowEndpoints
| order by Count desc
```

---

## Multi-workspace queries (ADX / Sentinel)

```kusto
// Query across multiple workspaces (ADX clusters or Log Analytics workspaces)
union
    workspace("workspace-1").requests,
    workspace("workspace-2").requests
| where timestamp > ago(1h)
| summarize Count = count() by cloud_RoleName
```

Post 19 covers the full cross-workspace patterns including Sentinel-specific tables.
