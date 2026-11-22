using Microsoft.Extensions.AI;

namespace DlqTriage.Tests;

/// A model that always says the same thing, and remembers what it was asked.
sealed class FakeChatClient(string reply) : IChatClient
{
    public List<ChatMessage> LastMessages { get; } = [];
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        LastMessages.Clear(); LastMessages.AddRange(messages); LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
