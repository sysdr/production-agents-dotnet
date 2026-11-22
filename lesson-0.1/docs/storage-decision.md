# Storage Decision Reference
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# PURPOSE
# This document maps data types to Azure storage services. It is updated
# as each storage layer is added to the system:
#   Post 14: Service Bus, Event Hubs, Blob, Table, ADX — decision framework
#   Post 16: Azurite Blob Storage — full integration
#   Post 17: Azurite Table Storage — full integration
#   Posts 40-43: Azure Data Explorer (Kusto Emulator) — full integration
#
# QUICK REFERENCE
#
#   Need              → Use
#   ─────────────────────────────────────────────────────────────────────
#   Process exactly once, DLQ on failure        → Service Bus queue
#   Fan-out to multiple consumers, replay       → Event Hubs
#   Store a file, image, or export archive      → Azure Blob Storage
#   Fast key-value lookup (userId, orderId)     → Azure Table Storage
#   Query time-series / aggregate logs at scale → Azure Data Explorer
#   ─────────────────────────────────────────────────────────────────────

---

## Service: Azure Service Bus

| Property | Value |
|---|---|
| **Data type** | Commands and notifications requiring guaranteed processing |
| **Pattern** | At-least-once delivery, PeekLock, DLQ after MaxDeliveryCount |
| **Scale** | ~10,000 messages/second per entity |
| **Retention** | Until `CompleteAsync` or `MaxDeliveryCount` exhaustion |
| **Local emulator** | `mcr.microsoft.com/azure-messaging/servicebus-emulator` |
| **Port** | 5672 (AMQP) |
| **Posts** | 7–11 (Phase 2 — complete) |

**Use when:** One consumer processes each message. Failures must be retried
(Polly in-process, then DeliveryCount increments, then DLQ on exhaustion).
Order processing, payment notifications, exactly-once side effects.

**Do not use when:** Multiple independent systems need the same event stream
(use Event Hubs), or the data is a large file (use Blob), or you need
to query historical data (use ADX).

---

## Service: Azure Event Hubs

| Property | Value |
|---|---|
| **Data type** | Events and telemetry for streaming fan-out |
| **Pattern** | Append-only partitioned log, consumer groups, per-event checkpoint |
| **Scale** | Millions of events/second across partitions |
| **Retention** | 1–90 days (configurable) |
| **Local emulator** | `mcr.microsoft.com/azure-messaging/eventhubs-emulator` |
| **Port** | 5673 (AMQP, host-mapped to avoid SB conflict) |
| **Posts** | 12–13 (Phase 2 — complete) |

**Use when:** Multiple independent consumers need the same event (audit log,
analytics, downstream processor). Event replay is required. Throughput
exceeds Service Bus limits.

**Do not use when:** Exactly-once delivery to a single consumer is required
(use Service Bus), or you need DLQ semantics (no DLQ in Event Hubs).

---

## Service: Azure Blob Storage

| Property | Value |
|---|---|
| **Data type** | Unstructured objects — files, images, archives, exports |
| **Pattern** | HTTP PUT/GET, immutable blobs, lifecycle tiers (hot/cool/archive) |
| **Scale** | Petabytes per account, unlimited objects |
| **Retention** | Until deleted or lifecycle policy expires |
| **Local emulator** | Azurite — `mcr.microsoft.com/azure-storage/azurite` |
| **Port** | 10000 (Blob), 10001 (Queue), 10002 (Table) |
| **Posts** | 16–17 (Phase 3 — upcoming) |
| **Connection string** | `UseDevelopmentStorage=true` |

**Use when:** Storing files, PDFs, CSVs, JSON exports, log archives, or any
binary content. Also for Event Hubs checkpoint storage (Post 13).

**Do not use when:** The data is structured and needs fast key-value lookup
(use Table Storage), or requires SQL joins (use Azure SQL), or needs
KQL aggregate queries (use ADX).

---

## Service: Azure Table Storage

| Property | Value |
|---|---|
| **Data type** | Semi-structured key-value with PartitionKey + RowKey |
| **Pattern** | OData queries, fast point lookups, no joins |
| **Scale** | Unlimited partitions, ~20,000 entities/second per partition |
| **Retention** | Until deleted |
| **Local emulator** | Azurite — same container as Blob |
| **Port** | 10002 (Table) |
| **Posts** | 16–17 (Phase 3 — upcoming) |

**Use when:** Storing session state, feature flags, configuration, or any
data where PartitionKey + RowKey defines the access pattern. Lookups are
always by known keys, not aggregate queries.

**Do not use when:** Joins are required, or the query pattern is unpredictable,
or you need aggregate analytics (use ADX or SQL).

---

## Service: Azure Data Explorer (ADX)

| Property | Value |
|---|---|
| **Data type** | Time-series, logs, telemetry — append-only at petabyte scale |
| **Pattern** | KQL queries, streaming ingest from Event Hubs, cold storage |
| **Scale** | Petabytes, sub-second queries on billions of rows |
| **Retention** | Configurable hot cache + cold storage (Azure Blob) |
| **Local emulator** | Kusto Emulator — `mcr.microsoft.com/azuredataexplorer/kustainer-developer` |
| **Port** | 8080 (query endpoint) |
| **Posts** | 40–43 (Phase 8 — upcoming) |

**Use when:** Aggregate time-series queries on log/event data that App Insights
cannot scale to. Long-term telemetry retention. Multi-source KQL dashboards
across App Insights, Azure Monitor, and custom data.

**Do not use when:** The data volume is below App Insights scale (use App Insights),
or transactional consistency is required (use SQL or Service Bus).

---

## Decision tree

```
What does the data need to do?

Is it a command that one consumer must process exactly once?
  Yes → Service Bus queue
  No  ↓

Does it need to be observed by multiple independent consumers?
  Yes → Event Hubs (streaming, fan-out, replay)
  No  ↓

Is it an unstructured large object (file, image, archive)?
  Yes → Azure Blob Storage
  No  ↓

Is it structured and always accessed by a known key?
  Yes → Azure Table Storage
  No  ↓

Does it need aggregate queries over time (KQL)?
  Yes → Azure Data Explorer
  No  → Re-evaluate: consider Azure SQL (not covered in this series)
```

---

## Local emulator quick-start

```powershell
# Start all emulators
docker compose up sqledge servicebus-emulator eventhubs-emulator azurite -d

# Verify (after ~35s for SQL Edge init)
curl http://localhost:10000/devstoreaccount1    # Azurite Blob (returns 400 = running)
curl http://localhost:55679/debug/pipelinez     # OTel Collector zPages
```

Connection strings (all local, all in appsettings.Development.json):

```json
{
  "ConnectionStrings": {
    "servicebus": "Endpoint=sb://localhost;...;UseDevelopmentEmulator=true;",
    "eventhubs":  "Endpoint=sb://localhost:5673;...;UseDevelopmentEmulator=true;",
    "azurite":    "UseDevelopmentStorage=true"
  }
}
```
