using System.Text.Json.Serialization;

namespace DlqTriage;

/// What we know about one dead-lettered message.
public sealed record DeadLetterInfo(string MessageId, string Reason, string Description, string Body);

[JsonConverter(typeof(JsonStringEnumConverter<TriageCategory>))]
public enum TriageCategory { Unknown, Transient, BadData, Bug }

/// What the model proposes. Our code decides what actually happens.
public sealed record TriageResult(TriageCategory Category, bool SafeToReplay, string Summary);
