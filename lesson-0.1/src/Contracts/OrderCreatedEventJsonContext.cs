// src/Contracts/OrderCreatedEventJsonContext.cs — POST 38 (updated)
// POST 38: Added [JsonSerializable(typeof(OrderPriority))] for the new enum.
// Without this, System.Text.Json source generator does not know how to
// serialise/deserialise OrderPriority at AOT compile time.
using System.Text.Json.Serialization;

namespace Contracts;

[JsonSerializable(typeof(OrderCreatedEvent))]
[JsonSerializable(typeof(OrderPriority))]   // POST 38: new enum type
[JsonSourceGenerationOptions(
    PropertyNamingPolicy   = JsonKnownNamingPolicy.CamelCase,
    WriteIndented          = false,
    // POST 38: WhenWritingNull ensures null Priority is omitted from wire format.
    // This is what makes null Priority → absent field, not "priority": null.
    // Old consumers never see the field; new consumers see it when set.
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // POST 38: UseStringEnumConverter serialises OrderPriority as "High"/"Normal"/"Low"
    // not as 1/0/2. String enum values are more readable in ADX KQL and Service Bus.
    Converters = [typeof(JsonStringEnumConverter<OrderPriority>)])]
public partial class OrderCreatedEventJsonContext : JsonSerializerContext { }
