// src/OrderService/FallbackServiceBusSender.cs — POST 37
// Resilient Service Bus publisher: primary queue with Polly fallback to secondary queue.

using Azure.Messaging.ServiceBus;
using Polly;
using Polly.Fallback;
using System.Text.Json;
using ServiceDefaults;

namespace OrderService;

/// <summary>
/// POST 37: Sends order events to Service Bus with automatic fallback.
/// If the primary queue sender throws, a Polly FallbackAction routes
/// the message to the fallback queue and records a warning span.
/// </summary>
public sealed class FallbackServiceBusSender : IAsyncDisposable
{
    private static readonly ResiliencePropertyKey<ServiceBusMessage> MessageKey =
        new("message");
    private static readonly ResiliencePropertyKey<string> TenantIdKey =
        new("tenantId");

    private readonly ServiceBusSender _primarySender;
    private readonly ServiceBusSender _fallbackSender;
    private readonly ResiliencePipeline<string> _pipeline;
    private readonly ILogger<FallbackServiceBusSender> _logger;

    public const string RouteTagPrimary  = "primary";
    public const string RouteTagFallback = "fallback";

    public FallbackServiceBusSender(
        ServiceBusClient client,
        ILogger<FallbackServiceBusSender> logger,
        string primaryQueueName  = "orders",
        string fallbackQueueName = "orders-fallback")
    {
        _primarySender  = client.CreateSender(primaryQueueName);
        _fallbackSender = client.CreateSender(fallbackQueueName);
        _logger         = logger;

        _pipeline = new ResiliencePipelineBuilder<string>()
            .AddFallback(new FallbackStrategyOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>()
                    .Handle<ServiceBusException>()
                    .Handle<OperationCanceledException>(),

                FallbackAction = async args =>
                {
                    if (!args.Context.Properties.TryGetValue(MessageKey, out var msg) || msg is null)
                        throw new InvalidOperationException("No message in context");

                    await _fallbackSender.SendMessageAsync(msg, args.Context.CancellationToken);

                    _logger.LogWarning(
                        "Primary Service Bus unavailable, order {OrderId} routed to fallback queue. " +
                        "Exception: {Exception}",
                        msg.MessageId,
                        args.Outcome.Exception?.Message);

                    return Outcome.FromResult(RouteTagFallback);
                },

                OnFallback = args =>
                {
                    var tenantId = args.Context.Properties.TryGetValue(TenantIdKey, out var tid)
                        ? tid ?? "unknown"
                        : "unknown";
                    _logger.LogWarning(
                        "ServiceBus.FallbackActivated TenantId={TenantId}",
                        tenantId);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public async Task<string> SendOrderAsync(
        string orderId,
        string tenantId,
        object orderPayload,
        CancellationToken ct = default)
    {
        var messageBody = JsonSerializer.SerializeToUtf8Bytes(orderPayload);
        var message = new ServiceBusMessage(messageBody)
        {
            MessageId   = orderId,
            ContentType = "application/json",
        };
        message.ApplicationProperties[TelemetryConstants.AttrTenantId] = tenantId;

        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(MessageKey, message);
        context.Properties.Set(TenantIdKey, tenantId);

        try
        {
            return await _pipeline.ExecuteAsync(
                async ctx =>
                {
                    await _primarySender.SendMessageAsync(message, ctx.CancellationToken);
                    return RouteTagPrimary;
                }, context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _primarySender.DisposeAsync();
        await _fallbackSender.DisposeAsync();
    }
}
