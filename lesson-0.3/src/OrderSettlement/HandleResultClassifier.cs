using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderSettlement;

// System.Text.Json writes a union as its case value with no type tag. Three object-shaped
// cases are ambiguous on read, so this classifier picks the case from the "kind" property.
public sealed class HandleResultClassifier : JsonTypeClassifierFactory<HandleResult>
{
    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options)
        => static (ref Utf8JsonReader reader) =>
        {
            if (reader.TokenType != JsonTokenType.StartObject) return null;

            var probe = reader; // Utf8JsonReader is a struct: scan a copy, leave the real reader where it is
            while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)
            {
                if (probe.TokenType != JsonTokenType.PropertyName) continue;
                var isKind = probe.ValueTextEquals("kind"u8);
                probe.Read();
                if (!isKind) { probe.TrySkip(); continue; }

                return probe.GetString() switch
                {
                    "processed" => typeof(Processed),
                    "retry" => typeof(Retry),
                    "dead-letter" => typeof(DeadLetter),
                    var other => throw new JsonException($"Unknown HandleResult kind '{other}'."),
                };
            }
            return null; // no "kind": let System.Text.Json report the ambiguity
        };
}
