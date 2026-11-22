// src/Contracts/OrderPriority.cs — POST 38
// Routing priority for order processing. Added to OrderCreatedEvent as a
// NULLABLE optional field to preserve backward and forward compatibility.
//
// SCHEMA EVOLUTION RULE:
//   New fields MUST be nullable (or have a default value) when added to
//   existing events. This ensures:
//
//   Backward compatibility (new producer → old consumer):
//     Old OrderProcessor does not know about OrderPriority.
//     System.Text.Json IGNORES unknown fields by default — the old consumer
//     successfully deserializes the message, OrderPriority is silently dropped.
//
//   Forward compatibility (old producer → new consumer):
//     Old OrderService sends a message without OrderPriority.
//     New OrderProcessor deserializes it: OrderPriority == null (field absent).
//     The processor treats null as Normal priority (safe default).
//
// BREAKING CHANGE ANTI-PATTERN (never do this in shared event contracts):
//   public required OrderPriority Priority { get; init; }  // ← BREAKING
//   If [JsonRequired] or 'required' is used on a NEW field,
//   old messages (which lack the field) throw JsonException on deserialization.
//   This causes the OrderProcessor to DLQ every message from the old producer.
namespace Contracts;

/// <summary>
/// POST 38: Processing priority for an order.
/// Null (absent in old messages) is treated as Normal by consumers.
/// </summary>
public enum OrderPriority
{
    Normal = 0,   // default — same-day processing
    High   = 1,   // expedited — within 4 hours
    Low    = 2    // deferred  — next-day batch
}
