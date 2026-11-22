using System.Text.Json.Serialization;

namespace OrderSettlement;

// What a message handler decided. Each case carries only what its settlement needs.
// "kind" is written on the wire so readers never have to guess the case from its shape.
public sealed record Processed(string OrderId)
{
    [JsonPropertyName("kind"), JsonPropertyOrder(-1)] public string Kind => "processed";
}

public sealed record Retry(string OrderId, TimeSpan After, string Reason)
{
    [JsonPropertyName("kind"), JsonPropertyOrder(-1)] public string Kind => "retry";
}

public sealed record DeadLetter(string OrderId, string Reason)
{
    [JsonPropertyName("kind"), JsonPropertyOrder(-1)] public string Kind => "dead-letter";
}

[JsonUnion(TypeClassifier = typeof(HandleResultClassifier))]
public union HandleResult(Processed, Retry, DeadLetter);
