// src/OrderProcessor/OrderProcessorWorker.cs
// CUMULATIVE — Post 10: wrap handler in Polly ResiliencePipeline.
//
// Post 8: basic ProcessMessageAsync loop with MaxConcurrentCalls
// Post 9: poison message detection + DeadLetterMessageAsync
// Post 10: ResiliencePipeline (retry + timeout) inside the handler NEW
//
// KEY PRINCIPLE: Polly retries happen INSIDE ProcessMessageAsync.
// The SDK does not know about them. From the SDK's perspective:
//   - If ProcessMessageAsync returns → call CompleteMessageAsync (AutoComplete=false: we call it)
//   - If ProcessMessageAsync throws → call AbandonMessageAsync → DeliveryCount++
//
// With Polly retry (MaxRetryAttempts=3) + transient failure:
//   Polly attempt 1: fails  ← NO DeliveryCount change
//   Polly attempt 2: fails  ← NO DeliveryCount change
//   Polly attempt 3: fails  ← NO DeliveryCount change
//   Polly retries exhausted → ProcessMessageAsync throws → SDK abandons → DeliveryCount = 2
//
// Without Polly, the same 3 transient failures:
//   SDK attempt 1: fails → DeliveryCount = 2
//   SDK attempt 2: fails → DeliveryCount = 3 → DLQ (MaxDeliveryCount=3)

using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using ServiceDefaults;

public class OrderProcessorWorker : BackgroundService
{
    private readonly ServiceBusProcessor _processor;
    private readonly ILogger<OrderProcessorWorker> _logger;
    private readonly OrderBlobStore? _blobStore;   // POST 16: nullable — runs without Azurite

    // POST 10: Build the resilience pipeline once — pipelines are thread-safe
    // and can be shared across concurrent message processing invocations.
    //
    // Pipeline order (innermost to outermost execution):
    //   Handler code
    //     → Timeout (abort if too slow)
    //       → Retry (retry on transient exceptions)
    //
    // Polly pipelines execute inner → outer on entry, outer → inner on exception.
    // The Timeout wraps each individual retry attempt, not the total duration.
    private readonly ResiliencePipeline _pipeline;

    public OrderProcessorWorker(
        ServiceBusProcessor processor,
        ILogger<OrderProcessorWorker> logger,
        OrderBlobStore? blobStore = null)   // POST 16: optional — null when Azurite not configured
    {
        _processor = processor;
        _logger    = logger;
        _blobStore = blobStore;

        _pipeline = new ResiliencePipelineBuilder()
            // POST 10: Retry policy — exponential backoff with jitter.
            //
            // Jitter prevents the "thundering herd" problem: if 100 concurrent
            // consumers all fail at the same time and all retry with identical
            // exponential delays, they all hammer the downstream service at the
            // same moments. Jitter spreads the retries randomly across the delay
            // window, smoothing the load.
            //
            // BackoffType.Exponential: delays are 1s, 2s, 4s (×2 each attempt)
            // UseJitter = true: adds ±25% random variation to each delay
            // ShouldHandle: only retry on transient exceptions — not on
            //   InvalidOperationException (that's the poison message throw) and
            //   not on OperationCanceledException (that's shutdown).
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromSeconds(1),
                ShouldHandle     = new PredicateBuilder()
                    .Handle<HttpRequestException>()       // downstream HTTP failure
                    .Handle<TimeoutRejectedException>(),  // Polly timeout (not handler timeout)
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Retrying order processing. Attempt={AttemptNumber} Delay={DelayMs}ms",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            // POST 10: Timeout — must be LESS than LockDuration (30s in servicebus-config.json).
            //
            // If the handler takes longer than the timeout, Polly throws
            // TimeoutRejectedException. The retry policy above catches it and retries.
            // After all retries, TimeoutRejectedException propagates to the SDK,
            // which calls AbandonMessageAsync. DeliveryCount increments by 1.
            //
            // WITHOUT a timeout: a handler that hangs for 35 seconds causes the
            // 30-second LockDuration to expire. The SDK loses the lock.
            // Service Bus makes the message visible again. DeliveryCount increments
            // even though no exception was thrown.
            .AddTimeout(TimeSpan.FromSeconds(25))   // < LockDuration (30s)
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync   += ProcessErrorAsync;
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        await _processor.StopProcessingAsync();
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        // POST 39: Extract the W3C traceparent injected by OrderService (Post 39)
        // and use it as the PARENT context for this span. This is what makes
        // "ProcessOrderMessage" a CHILD span of "PublishOrderEvent" instead of
        // an unrelated new trace — the entire order lifecycle becomes one TraceId.
        var parentContext = TraceContextPropagator.ExtractTraceContext(
            args.Message.ApplicationProperties);

        using var activity = Extensions.ActivitySource.StartActivity(
            "ProcessOrderMessage",
            ActivityKind.Consumer,
            parentContext);

        OrderCreatedEvent? order;
        try
        {
            order = args.Message.Body.ToObjectFromJson<OrderCreatedEvent>(
                OrderCreatedEventJsonContext.Default.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deserialisation failed. {MessageId}", args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "DeserializationFailed",
                deadLetterErrorDescription: ex.Message);
            return;
        }

        activity?.SetTag(TelemetryConstants.AttrOrderId,  order.OrderId);
        activity?.SetTag(TelemetryConstants.AttrTenantId, order.TenantId);

        if (order.OrderId.StartsWith("POISON_", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Poison message detected. {OrderId} DeliveryCount={DC}",
                order.OrderId, args.Message.DeliveryCount);
            // Do NOT retry poison messages — dead-letter immediately
            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: "PoisonMessage",
                deadLetterErrorDescription: "OrderId has POISON_ prefix — will never succeed");
            return;
        }

        _logger.LogInformation("Processing order. {OrderId} {TenantId} DeliveryCount={DC}",
            order.OrderId, order.TenantId, args.Message.DeliveryCount);

        // POST 10: Execute handler inside the Polly pipeline.
        // If ExecuteAsync throws (retries exhausted), it propagates to ProcessMessageAsync
        // which propagates to the SDK, which calls AbandonMessageAsync.
        // DeliveryCount increments by 1 — for all Polly retry attempts combined.
        await _pipeline.ExecuteAsync(async ct =>
        {
            await ProcessOrderCoreAsync(order, ct);
        }, args.CancellationToken);

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);

        // POST 16: Write processed order record to Blob Storage AFTER CompleteAsync.
        // Sequence matters: Complete first, then write to Blob.
        //   If Blob write fails: the message is gone (Completed) but the blob is missing.
        //     → Acceptable: the event is still in Event Hubs (Post 12). Blob can be
        //       rehydrated from Event Hubs replay if needed.
        //   If we wrote to Blob first and Completer failed: the blob exists but the
        //     message is re-delivered on next poll. Duplicate blob write handled
        //     by overwrite: false in OrderBlobStore.
        //   The Complete-first order makes the Service Bus side authoritative.
        if (_blobStore is not null)
        {
            await _blobStore.WriteProcessedOrderAsync(
                order,
                args.Message.DeliveryCount,
                args.CancellationToken);
        }

        _logger.LogInformation("Order processed. {OrderId}", order.OrderId);
    }

    // POST 10: The core handler — separated so the pipeline can wrap it cleanly.
    // In production this would call a downstream service (payment processor, inventory).
    // Here we simulate a transient failure for the Explore it section.
    private static int _failCount = 0;

    private async Task ProcessOrderCoreAsync(OrderCreatedEvent order, CancellationToken ct)
    {
        // TRANSIENT_FAIL prefix: simulate failure for first 2 attempts, succeed on 3rd
        if (order.OrderId.StartsWith("TRANSIENT_", StringComparison.OrdinalIgnoreCase))
        {
            var attempt = Interlocked.Increment(ref _failCount);
            if (attempt % 3 != 0)   // fail on attempts 1, 2; succeed on 3
            {
                _logger.LogWarning("Simulated transient failure. {OrderId} Attempt={Attempt}",
                    order.OrderId, attempt % 3);
                throw new HttpRequestException(
                    $"Simulated 503 from downstream. OrderId={order.OrderId}");
            }
        }

        // Normal processing
        await Task.Delay(Random.Shared.Next(50, 300), ct);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "ServiceBusProcessor error. {EntityPath} Source={ErrorSource}",
            args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }
}
