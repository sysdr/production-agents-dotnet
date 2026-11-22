// src/OrderEventProcessor/OrderEventProcessorWorker.cs
// POST 13: EventProcessorClient with per-event checkpointing (War Story #3 fix).
//
// War Story #3 failure: EventHubConsumerClient with time-based checkpointing
//   (every 60s) → partition rebalance → consumer starts 20 minutes behind.
//
// Post 13 fix: EventProcessorClient with per-event checkpointing via Azurite.
//   On restart: reads last checkpoint Offset from Azurite blob → resumes from
//   next event, not from the beginning. Replay cost: at most 1 event.

using System.Diagnostics;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDefaults;

public class OrderEventProcessorWorker : BackgroundService
{
    private readonly EventProcessorClient _processor;
    private readonly ILogger<OrderEventProcessorWorker> _logger;

    public OrderEventProcessorWorker(
        EventProcessorClient processor,
        ILogger<OrderEventProcessorWorker> logger)
    {
        _processor = processor;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessEventAsync       += ProcessEventAsync;
        _processor.ProcessErrorAsync       += ProcessErrorAsync;
        _processor.PartitionInitializingAsync += PartitionInitializingAsync;
        _processor.PartitionClosingAsync   += PartitionClosingAsync;

        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation(
            "EventProcessorClient started — consumer group: order-processor");

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        await _processor.StopProcessingAsync();
    }

    private async Task ProcessEventAsync(ProcessEventArgs args)
    {
        // MaximumWaitTime fires this with an empty event for heartbeat.
        // Skip processing but DO NOT checkpoint empty events.
        if (args.Data is null) return;

        // POST 39: Extract traceparent from EventData.Properties — same pattern
        // as the Service Bus consumer side. This makes ProcessOrderEvent a child
        // of the same PublishOrderEvent span, joining both consumer paths
        // (Service Bus and Event Hubs) into one TraceId per order.
        var parentContext = TraceContextPropagator.ExtractTraceContext(
            args.Data.Properties);

        using var activity = Extensions.ActivitySource.StartActivity(
            "ProcessOrderEvent",
            ActivityKind.Consumer,
            parentContext);

        OrderCreatedEvent? order;
        try
        {
            order = args.Data.EventBody
                .ToObjectFromJson<OrderCreatedEvent>(
                    OrderCreatedEventJsonContext.Default.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EH event deserialisation failed. " +
                "Partition={PartitionId} Offset={Offset}",
                args.Partition.PartitionId, args.Data.OffsetString);
            // POST 13: Unlike Service Bus, there is no DLQ.
            // For non-deserializable events: log, checkpoint past the event,
            // and continue. The raw bytes can be recovered from the partition
            // up to the retention period.
            await args.UpdateCheckpointAsync(args.CancellationToken);
            return;
        }

        activity?.SetTag(TelemetryConstants.AttrOrderId,  order.OrderId);
        activity?.SetTag(TelemetryConstants.AttrTenantId, order.TenantId);

        _logger.LogInformation(
            "EH order received. {OrderId} {TenantId} " +
            "Partition={PartitionId} Offset={Offset} Seq={SequenceNumber}",
            order.OrderId, order.TenantId,
            args.Partition.PartitionId,
            args.Data.OffsetString,
            args.Data.SequenceNumber);

        // Simulate processing
        await Task.Delay(50, args.CancellationToken);

        // POST 13: PER-EVENT CHECKPOINT — the War Story #3 fix.
        //
        // UpdateCheckpointAsync writes the current event's Offset and
        // SequenceNumber to the Azurite blob for this partition.
        // On restart, EventProcessorClient reads this checkpoint and
        // calls ProcessEventAsync starting from Offset + 1.
        //
        // Cost: one Azurite blob write per event.
        // For high-throughput: checkpoint every N events instead:
        //   if (++_count % 100 == 0) await args.UpdateCheckpointAsync(...)
        await args.UpdateCheckpointAsync(args.CancellationToken);

        _logger.LogInformation(
            "EH checkpoint updated. {OrderId} Partition={PartitionId} Offset={Offset}",
            order.OrderId,
            args.Partition.PartitionId,
            args.Data.OffsetString);
    }

    private Task PartitionInitializingAsync(PartitionInitializingEventArgs args)
    {
        // POST 13: TenantId-to-partition routing insight.
        // When this processor claims a partition, log which partition it owns.
        // In a multi-instance deployment, different processor instances own
        // different partitions — enabling horizontal scaling.
        _logger.LogInformation(
            "Partition claimed. PartitionId={PartitionId} StartingPosition={Position}",
            args.PartitionId,
            args.DefaultStartingPosition);
        return Task.CompletedTask;
    }

    private Task PartitionClosingAsync(PartitionClosingEventArgs args)
    {
        _logger.LogInformation(
            "Partition released. PartitionId={PartitionId} Reason={Reason}",
            args.PartitionId,
            args.Reason);
        return Task.CompletedTask;
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "EventProcessorClient error. " +
            "Partition={PartitionId} Operation={Operation}",
            args.PartitionId, args.Operation);
        return Task.CompletedTask;
    }
}
