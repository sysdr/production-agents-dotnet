# SRE Runbook — Hyperscale Log Monitoring
# Distributed Systems with .NET — Post 23
#
# PURPOSE: Operational response playbooks for every alert on the SRE dashboard.
# The dashboard shows the signal; this runbook shows the response.
#
# FOUR GOLDEN SIGNALS (Google SRE Book):
#   1. LATENCY    — how long requests take
#   2. TRAFFIC    — how many requests per unit time
#   3. ERRORS     — what fraction of requests fail
#   4. SATURATION — how "full" is the service (queue depth, retry rate)
#
# ALERT ESCALATION:
#   Severity 1 (S1): Customer impact confirmed — page the on-call engineer immediately
#   Severity 2 (S2): Degradation detected — respond within 30 minutes
#   Severity 3 (S3): Warning threshold crossed — respond within 4 hours

---

## Signal: p99 Latency — /orders/create

**Dashboard tile:** "p99 Latency — /orders/create"
**KQL source:** `docs/kql-queries.md` — "p99 latency by endpoint"
**Alert threshold:** > 1000ms for 5 consecutive 5-minute buckets
**Severity:** S2

### Immediate steps (first 5 minutes)

1. Check the "p99 Dependency Latency — SB and EH" tile on the same dashboard.
   - If SB send p99 > 500ms: Service Bus is throttling. Check SB namespace metrics in Azure Portal.
   - If EH publish p99 > 500ms: Event Hubs is throttling. Check EH namespace throughput units.
   - If both SB and EH are normal: latency is inside `OrderService`.

2. Open App Insights → Failures → Dependencies. Filter to the last 30 minutes.
   - Sort by Duration. The slowest dependencies identify the bottleneck.

3. Run the cross-table correlation query (`docs/kql-queries.md` — "End-to-end order journey"):
   ```kusto
   requests
   | where timestamp > ago(30m)
   | where name has "orders/create"
   | where duration > 500
   | extend OrderId = tostring(customDimensions.OrderId)
   | join kind=inner (
       traces | where message has "Order processed"
       | extend OrderId = tostring(customDimensions.OrderId)
       | project CompletedAt = timestamp, OrderId
   ) on OrderId
   | extend EndToEndMs = datetime_diff('millisecond', CompletedAt, timestamp)
   | order by EndToEndMs desc
   ```

### Root causes and fixes

| Symptom | Root cause | Fix |
|---|---|---|
| SB send > 500ms | Service Bus namespace throttled | Scale up SB tier or reduce message rate |
| EH publish > 500ms | Event Hubs throughput units exhausted | Add throughput units |
| /orders/create slow, SB/EH normal | OrderService CPU or connection pool | Scale out OrderService replicas |
| Slow for one tenant only | Tenant-specific config issue or rate limit | Check TenantConfigStore.MaxOrdersPerHour |

---

## Signal: Error Rate — all services

**Dashboard tile:** "Error Rate by Service"
**KQL source:** `docs/kql-queries.md` — "Error rate by endpoint"
**Alert threshold:** > 1% for 5 consecutive minutes
**Severity:** S1 if > 5% for any 5-minute bucket; S2 if 1–5%

### Immediate steps

1. Identify which service has elevated error rate. The tile shows `cloud_RoleName`.
   - `order-service`: check JWT authentication issues (401 spike) or SB connection failures
   - `order-processor`: check DLQ depth tile — elevated errors here usually precede DLQ growth
   - `user-service`: check `users/login-failed` error rate — could be brute-force attack

2. Run the exception query (`docs/kql-queries.md` — "Exceptions with full stack trace"):
   ```kusto
   exceptions
   | where timestamp > ago(30m)
   | project timestamp, type, innermostMessage, cloud_RoleName, operation_Id
   | order by timestamp desc
   ```
   Note the exception `type` — this is the primary classifier for root cause.

3. For `Azure.RequestFailedException`: check Azure service status at status.azure.com

### Common exception types and root causes

| Exception type | Likely cause | Response |
|---|---|---|
| `ServiceBusException` | SB emulator or Azure SB namespace unavailable | Check SB namespace health; verify emulator running |
| `RequestFailedException` (SB) | SB connection string wrong or expired | Rotate SB shared access key |
| `EventHubsException` | EH namespace throttled or unavailable | Check EH throughput units |
| `SecurityTokenExpiredException` | JWT token expired (> 1h old) | Token expiry is expected — client should re-acquire |
| `SecurityTokenInvalidSignatureException` | Wrong signing key or token tampered | Check AppHost Jwt:SigningKey consistency |
| `RequestFailedException` (Table) | Azurite not running or Azure Table unavailable | Restart Azurite; check Azure Storage account |

---

## Signal: Service Bus DLQ Depth

**Dashboard tile:** "Service Bus DLQ Depth"
**KQL source:** `docs/sentinel-detection-rules.md` Rule 3 (DLQ accumulation)
**Alert threshold:** > 0 for any 5-minute bucket
**Severity:** S2 (immediate response — messages are failing and accumulating)

### Context

A non-zero DLQ means messages reached `MaxDeliveryCount` (3 in this series) without being processed. Unaddressed DLQ growth leads to:
1. Lost order events (DLQ entries are not replayed automatically)
2. Discrepancy between SB message count and Blob Storage blob count
3. Business impact if orders are not processed

### Immediate steps

1. Identify the failing message. Run:
   ```kusto
   traces
   | where timestamp > ago(1h)
   | where message has "DLQ non-empty" or message has "Poison message"
   | project timestamp, message, cloud_RoleName,
             DLQ = tostring(customDimensions.DeadLetterCount),
             OrderId = tostring(customDimensions.OrderId)
   | order by timestamp desc
   ```

2. Check if the failure is consistent (all orders failing) or selective (specific orders):
   - All orders failing: infrastructure issue (SB disconnected, downstream service down)
   - Specific orders failing: data issue (malformed payload, `create-poison` test endpoint called)

3. Inspect the DLQ message directly:
   - Azure Portal → Service Bus namespace → Queues → orders → Dead Letter → peek messages
   - Look at `DeadLetterReason` and `DeadLetterErrorDescription` properties

4. Fix and replay:
   - Fix the root cause first (do not replay into a still-broken processor)
   - Replay DLQ messages via Azure Service Bus Explorer or the Portal "Replay" action

### DLQ replay KQL query (confirm replays are processing)
```kusto
traces
| where timestamp > ago(30m)
| where message has "Processing order"
| extend DC = toint(customDimensions.DC)
| where DC > 1   // replayed messages have DC=2 or DC=3
| project timestamp, message, DC, operation_Id
| order by timestamp desc
```

---

## Signal: Polly Retry Rate

**Dashboard tile:** "Polly Retry Rate"
**KQL source:** `docs/sentinel-detection-rules.md` Rule 5
**Alert threshold:** > 5 retry events per 5-minute bucket for 10 consecutive minutes
**Severity:** S3 (warning — not yet causing failures, but degrading throughput)

### Context

Polly retries indicate transient failures in the order processing pipeline. Low retry rates (< 5/min) are normal and expected — transient network blips, brief downstream unavailability. High sustained retry rates indicate the system is spending significant time retrying rather than processing.

A sustained high retry rate precedes DLQ accumulation: if Polly retries are exhausted without success, `MaxDeliveryCount` is reached and the message dead-letters.

### Immediate steps

1. Check whether retries are succeeding (orders eventually processed) or failing (leading to DLQ):
   ```kusto
   let retryWindow = ago(30m);
   let retries = traces | where timestamp > retryWindow | where message has "Retrying"
       | summarize RetryCount=count() by bin(timestamp, 5m);
   let dlq = traces | where timestamp > retryWindow | where message has "DLQ non-empty"
       | summarize DLQCount=count() by bin(timestamp, 5m);
   retries | join kind=leftouter (dlq) on timestamp
   | project timestamp, RetryCount, DLQCount=coalesce(DLQCount, 0)
   ```

2. If retries are succeeding (no DLQ growth): monitor for 30 minutes, no immediate action required
3. If DLQ is growing alongside retries: treat as DLQ incident (see above)

---

## Signal: Traffic — Order Volume Drop

**Dashboard tile:** "Order Volume — /orders/create (5m buckets)"
**Alert threshold:** < 10% of rolling 7-day average for 15 consecutive minutes
**Severity:** S1 if volume drops to 0; S2 if > 50% drop

### Context

A traffic drop is often the first signal of an upstream issue. `OrderService` is healthy, but fewer orders are arriving. This is not a service failure — it is a symptom of something broken upstream (API gateway, client application, load balancer, DNS).

### Immediate steps

1. Confirm it is a traffic drop (not a measurement failure):
   ```kusto
   requests
   | where timestamp > ago(30m)
   | where name has "orders/create"
   | summarize Count=count(), Errors=countif(success==false)
     by bin(timestamp, 5m)
   ```
   If Count = 0 for 15+ minutes but the service is healthy: upstream issue.

2. Check the authentication layer — 401 spikes reduce successful order volume:
   ```kusto
   requests
   | where timestamp > ago(30m)
   | where name has "orders/create"
   | summarize by bin(timestamp, 5m), success
   ```

3. Check external dependencies: API gateway logs, CDN, load balancer health probes.

---

## Escalation contacts

| Escalation level | Contact | SLA |
|---|---|---|
| S1 — page immediately | On-call engineer (PagerDuty rotation) | Respond within 5 minutes |
| S2 — respond within 30 min | Team Slack: #incidents | Acknowledge within 30 minutes |
| S3 — respond within 4 hours | Team Slack: #alerts | Review at next daily standup |
| Azure service outage | Azure support ticket | As per support tier SLA |

## Post-incident review (PIR) checklist

After every S1 incident:
- [ ] Timeline reconstructed from App Insights traces and KQL queries
- [ ] Root cause identified (not just the symptom)
- [ ] `docs/sre-runbook.md` updated with new failure mode if not already present
- [ ] `docs/sentinel-detection-rules.md` updated if new detection rule would have caught it earlier
- [ ] Azure Monitor alert rule created if not already in place
