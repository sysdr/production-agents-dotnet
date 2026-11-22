using Microsoft.Extensions.AI;

namespace DlqTriage;

public sealed class DlqTriager(IChatClient chat, int maxBodyChars = 500)
{
    const string Instructions = """
        You triage messages from an Azure Service Bus dead-letter queue.
        Pick one category:
        - Transient: the message is fine, a dependency was down or slow.
        - BadData: the message itself is wrong (bad JSON, missing field, unknown value).
        - Bug: the message is fine but our handler fails on it.
        - Unknown: you cannot tell.
        SafeToReplay is true only for Transient.
        Summary: one sentence a tired on-call engineer can act on.
        """;

    public async Task<TriageResult> TriageAsync(DeadLetterInfo m, CancellationToken ct = default)
    {
        // Send only what the model needs. Bodies can be huge and can hold personal data.
        var body = m.Body.Length > maxBodyChars ? m.Body[..maxBodyChars] + " …(truncated)" : m.Body;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions),
            new(ChatRole.User, $"""
                Reason: {m.Reason}
                Description: {m.Description}
                Body: {body}
                """),
        ];

        try
        {
            // Asks the model for JSON matching TriageResult and parses it for us.
            var response = await chat.GetResponseAsync<TriageResult>(messages, cancellationToken: ct);
            if (!response.TryGetResult(out var proposed) || proposed is null)
                return Fallback("Model reply was not valid triage JSON.");

            // The model proposes, our code decides: only Transient may be replayed.
            return proposed with { SafeToReplay = proposed.Category == TriageCategory.Transient && proposed.SafeToReplay };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fallback($"Triage failed: {e.GetType().Name}.");
        }
    }

    static TriageResult Fallback(string why) => new(TriageCategory.Unknown, false, why);
}
