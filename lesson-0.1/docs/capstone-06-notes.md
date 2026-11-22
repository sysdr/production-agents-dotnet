# Capstone 06 — Schema Evolution: Compatibility Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 38
#
# ── THE TWO COMPATIBILITY PROPERTIES ─────────────────────────────────────
#
# Backward compatibility (new producer → old consumer):
#   A consumer running the OLD version of the contract can process
#   messages produced by the NEW version.
#   In this post: old OrderProcessor ignores "orderPriority" field.
#   System.Text.Json does this by default (unknown fields are skipped).
#
# Forward compatibility (old producer → new consumer):
#   A consumer running the NEW version of the contract can process
#   messages produced by the OLD version.
#   In this post: new OrderProcessor receives a message without "orderPriority".
#   Since Priority is nullable (OrderPriority?), STJ sets it to null — no error.
#   The processor treats null as Normal priority (safe default).
#
# ── THE FOUR SAFE SCHEMA CHANGES ─────────────────────────────────────────
#
# SAFE — Adding a nullable field (this post):
#   public OrderPriority? Priority { get; init; }
#   With WhenWritingNull: absent from wire when null → old consumers unaffected.
#   On new consumers receiving old messages: field is null → treat as default.
#
# SAFE — Adding a field with a default value:
#   public string Region { get; init; } = "us-east-1";
#   Old messages missing Region → default value "us-east-1" applied.
#   New messages with Region → value used as-is.
#
# SAFE — Adding a new event type (parallel topic or new message type name):
#   OrderCreatedEventV2 alongside OrderCreatedEvent.
#   Old consumers only subscribe to OrderCreatedEvent topic.
#   New consumers subscribe to both (handles migration window).
#
# SAFE — Widening a type (e.g. int → long):
#   If the JSON serialiser handles the coercion (STJ does for numeric widening).
#
# ── THE FOUR BREAKING SCHEMA CHANGES ─────────────────────────────────────
#
# BREAKING — Adding a required field (never add [JsonRequired] to new field):
#   public required OrderPriority Priority { get; init; }
#   Old messages without Priority → JsonException on deserialization → DLQ.
#   Every v1 message produced during rollout goes to the DLQ.
#
# BREAKING — Renaming a field:
#   "tenantId" → "tenant_id"
#   Old producer sends "tenantId" — new consumer sees null for "tenant_id".
#   If TenantId is required, DLQ immediately. If nullable, silent data loss.
#   Fix: use [JsonPropertyName("tenantId")] on old AND new name, handle both.
#
# BREAKING — Changing a field's type (narrowing or incompatible change):
#   AmountCents: long → string
#   Old consumers receive a string where they expected long → parse error → DLQ.
#
# BREAKING — Removing a field that consumers depend on:
#   Remove TenantId from OrderCreatedEvent.
#   Old consumers that read TenantId get null → incorrect downstream behaviour.
#
# ── THE EventVersion PATTERN ─────────────────────────────────────────────
#
# Incrementing EventVersion on each schema change enables consumers to
# branch their processing logic:
#
#   switch (evt.EventVersion)
#   {
#       case 1:
#           // Old format — no priority, treat as Normal
#           var priority = OrderPriority.Normal;
#           break;
#       case 2:
#           // New format — explicit priority or null (treat null as Normal)
#           var priority = evt.Priority ?? OrderPriority.Normal;
#           break;
#       default:
#           logger.LogWarning("Unknown event version {V}", evt.EventVersion);
#           var priority = OrderPriority.Normal;  // safe default
#           break;
#   }
#
# This is the "versioned event" pattern. It is simpler than maintaining
# parallel Kafka/Service Bus topics (one per version) for minor additive changes.
# Use parallel topics only when the schema change is truly incompatible.
#
# ── DETECTING MIGRATION PROGRESS IN ADX ──────────────────────────────────
#
# During a rolling deployment (old → new OrderService), both v1 and v2
# messages co-exist in the Service Bus queue. ADX tracks the migration:
#
#   OTelLogs
#   | where Timestamp > ago(24h)
#   | where Body contains "Processing order"
#   | extend Version = toint(Attributes["event.version"])
#   | summarize count() by Version, bin(Timestamp, 1h)
#   | render timechart
#
# When the v1 line reaches zero, all OrderService pods have been updated
# to the new image (post-deploy confirmation).
#
# ── STJ CONFIGURATION FOR SAFE EVOLUTION ─────────────────────────────────
#
# In OrderCreatedEventJsonContext:
#   DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
#     → null Priority is not written to wire format
#     → old consumers never see the null field (they'd store "orderPriority": null)
#
#   Converters = [typeof(JsonStringEnumConverter<OrderPriority>)]
#     → Priority serialised as "High"/"Normal"/"Low" not 0/1/2
#     → Adding new enum values in a future v3 won't break old consumers
#       that expect an unknown string (they see "Express" → ignore or default)
#
# WITHOUT JsonStringEnumConverter (using integer enums):
#   If a new value is added: OrderPriority.Express = 3
#   Old consumers receive integer 3 → fails enum deserialization (ArgumentException)
#   String enum values are always safer for extensible enums in shared contracts.
