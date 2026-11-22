using System.Text.Json;
using OrderSettlement;
using Xunit;

public sealed record Shipped(string OrderId);
public sealed record Cancelled(string OrderId);
public union NoTagStatus(Shipped, Cancelled); // no classifier: what you get by default

public class SettlementTests
{
    [Fact]
    public void Each_case_maps_to_one_settlement()
    {
        Assert.Equal(SettleVerb.Complete, Settlement.Decide(new Processed("o-1")).Verb);
        Assert.Equal(SettleVerb.Abandon, Settlement.Decide(new Retry("o-2", TimeSpan.FromSeconds(30), "payment 503")).Verb);
        Assert.Equal(SettleVerb.DeadLetter, Settlement.Decide(new DeadLetter("o-3", "bad tenant")).Verb);
    }

    [Theory]
    [InlineData("processed")]
    [InlineData("retry")]
    [InlineData("dead-letter")]
    public void Json_round_trip_keeps_the_case(string kind)
    {
        HandleResult original = kind switch
        {
            "processed" => new Processed("o-1"),
            "retry" => new Retry("o-1", TimeSpan.FromSeconds(30), "payment 503"),
            _ => new DeadLetter("o-1", "bad tenant"),
        };

        var json = JsonSerializer.Serialize(original);
        Assert.StartsWith($"{{\"kind\":\"{kind}\"", json);

        var back = JsonSerializer.Deserialize<HandleResult>(json);
        Assert.Equal(original.Value, back.Value);
    }

    [Fact]
    public void Without_a_classifier_object_cases_are_ambiguous()
    {
        var json = JsonSerializer.Serialize<NoTagStatus>(new Shipped("o-1"));
        Assert.Equal("{\"OrderId\":\"o-1\"}", json); // no type information on the wire
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NoTagStatus>(json));
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public void Unknown_kind_fails_loudly()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HandleResult>("{\"kind\":\"defer\",\"OrderId\":\"o-1\"}"));
}
