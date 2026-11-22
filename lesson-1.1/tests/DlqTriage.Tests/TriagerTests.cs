using Microsoft.Extensions.AI;

namespace DlqTriage.Tests;

public class TriagerTests
{
    static readonly DeadLetterInfo Gift = new("ord-1042", "MaxDeliveryCountExceeded",
        "Message could not be consumed after 10 delivery attempts.",
        """{"orderId":1042,"channel":"Gift","amountCents":1999}""");

    [Fact]
    public async Task Valid_reply_becomes_a_typed_result()
    {
        var fake = new FakeChatClient("""{"category":"BadData","safeToReplay":false,"summary":"Unknown channel Gift."}""");
        var r = await new DlqTriager(fake).TriageAsync(Gift);
        Assert.Equal(TriageCategory.BadData, r.Category);
        Assert.Equal("Unknown channel Gift.", r.Summary);
    }

    [Fact]
    public async Task Garbage_reply_falls_back_to_unknown()
    {
        var fake = new FakeChatClient("Sure! Here is my analysis of the message...");
        var r = await new DlqTriager(fake).TriageAsync(Gift);
        Assert.Equal(TriageCategory.Unknown, r.Category);
        Assert.False(r.SafeToReplay);
    }

    [Fact]
    public async Task Code_overrules_the_model_on_replay()
    {
        var fake = new FakeChatClient("""{"category":"BadData","safeToReplay":true,"summary":"Replay it."}""");
        var r = await new DlqTriager(fake).TriageAsync(Gift);
        Assert.False(r.SafeToReplay);
    }

    [Fact]
    public async Task Transient_may_be_replayed()
    {
        var fake = new FakeChatClient("""{"category":"Transient","safeToReplay":true,"summary":"Payments API was down."}""");
        var r = await new DlqTriager(fake).TriageAsync(Gift);
        Assert.True(r.SafeToReplay);
    }

    [Fact]
    public async Task Asks_for_json_with_a_schema()
    {
        var fake = new FakeChatClient("""{"category":"Bug","safeToReplay":false,"summary":"x"}""");
        await new DlqTriager(fake).TriageAsync(Gift);
        var format = Assert.IsType<ChatResponseFormatJson>(fake.LastOptions?.ResponseFormat);
        Assert.NotNull(format.Schema);
    }

    [Fact]
    public async Task Long_bodies_are_cut_before_they_reach_the_model()
    {
        var big = Gift with { Body = new string('x', 10_000) };
        var fake = new FakeChatClient("""{"category":"Bug","safeToReplay":false,"summary":"x"}""");
        await new DlqTriager(fake, maxBodyChars: 500).TriageAsync(big);
        var prompt = fake.LastMessages.Last().Text;
        Assert.Contains("MaxDeliveryCountExceeded", prompt);
        Assert.True(prompt.Length < 1_000, $"prompt was {prompt.Length} chars");
    }
}
