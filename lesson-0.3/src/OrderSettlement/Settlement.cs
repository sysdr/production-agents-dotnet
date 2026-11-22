namespace OrderSettlement;

public enum SettleVerb { Complete, Abandon, DeadLetter }

public sealed record SettlementAction(SettleVerb Verb, string OrderId, TimeSpan? Delay = null, string? Reason = null);

public static class Settlement
{
    // Exhaustive by construction: add a case to HandleResult and this stops compiling (CS8509 is an error).
    public static SettlementAction Decide(HandleResult result) => result switch
    {
        Processed p => new(SettleVerb.Complete, p.OrderId),
        Retry r => new(SettleVerb.Abandon, r.OrderId, r.After, r.Reason),
        DeadLetter d => new(SettleVerb.DeadLetter, d.OrderId, Reason: d.Reason),
    };
}
