// src/Contracts/OrderCreatedEvent.cs — POST 38 (updated from POST 7)
// SCHEMA CHANGE: Added OrderPriority? — nullable so old messages still deserialize.
//
// WIRE FORMAT COMPATIBILITY:
//   Old message (v1):  {"orderId":"…","tenantId":"…","createdAt":"…","eventVersion":1}
//   New message (v2):  {"orderId":"…","tenantId":"…","createdAt":"…","eventVersion":2,
//                       "orderPriority":"High"}
//
//   v1 message → new OrderProcessor: orderPriority is absent → null (safe, treated as Normal)
//   v2 message → old OrderProcessor: orderPriority field is ignored (STJ default) → no error
//
// WHY EventVersion EXISTS:
//   EventVersion allows consumers to switch behaviour by version.
//   EventVersion=1 messages have no priority and should be treated as Normal.
//   EventVersion=2 messages have explicit priority that must be respected.
//   This is the "versioned event" pattern — simpler than parallel topics for
//   minor additive changes.
namespace Contracts;

public sealed record OrderCreatedEvent
{
    public required string OrderId   { get; init; }
    public required string TenantId  { get; init; }
    public required string CreatedAt { get; init; }

    // POST 38: Bumped from 1 → 2 when OrderPriority was added.
    // Consumers can branch: if (evt.EventVersion < 2) treat as Normal.
    public int EventVersion { get; init; } = 2;

    public long   AmountCents { get; init; }
    public string Currency    { get; init; } = "USD";

    // POST 38: Nullable — null means "field absent" (old v1 message).
    // JsonIgnoreCondition.WhenWritingNull (set in JsonContext) ensures
    // null priority is omitted from the wire format, keeping v2 messages
    // backward-compatible with v1 consumers that would choke on "orderPriority":null.
    public OrderPriority? Priority { get; init; }
}
