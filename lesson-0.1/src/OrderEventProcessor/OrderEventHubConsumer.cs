// src/OrderEventProcessor/OrderEventHubConsumer.cs
// POST 12: Reads OrderCreatedEvent from the "order-events" Event Hub.
//
// KEY DIFFERENCE from Service Bus:
//   Service Bus: each message delivered to ONE consumer (queue) or
//     ONE consumer per subscription (topic). Message is gone after Complete.
//   Event Hubs: ALL consumer groups read ALL events independently.
//     Events are NOT removed after reading — they stay in the partition
//     for the retention period (DefaultMessageTimeToLive in config).
//
// CHECKPOINTING:
//   EventHubConsumerClient.ReadEventsAsync() reads from the beginning of
//   the partition (or from a stored checkpoint). Per-event checkpointing
//   (calling UpdateCheckpointAsync after each event) is the War Story #3 fix.
//   Post 13 implements full per-event checkpointing with Azurite as the
//   checkpoint store. This post demonstrates the read pattern.

using Azure.Messaging.EventHubs.Consumer;
using Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceDefaults;

public class OrderEventHubConsumer : BackgroundService
{
    private readonly EventHubConsumerClient _consumer;
    private readonly ILogger<OrderEventHubConsumer> _logger;

    public OrderEventHubConsumer(
        EventHubConsumerClient consumer,
        ILogger<OrderEventHubConsumer> logger)
    {
        _consumer = consumer;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OrderEventHubConsumer started — reading from order-events / $Default");

        // ReadEventsAsync yields events from ALL partitions in sequence.
        // Partition ordering: events within a single partition are ordered.
        // Cross-partition ordering is NOT guaranteed.
        //
        // ReadEventsFromPartitionAsync(partitionId, ...) reads a single
        // partition — used when you need partition-specific processing.
        // Post 13 uses this for TenantId-based partition routing.
        await foreach (var partitionEvent in
            _consumer.ReadEventsAsync(stoppingToken))
        {
            using var activity = Extensions.ActivitySource
                .StartActivity("ConsumeOrderEvent");

            OrderCreatedEvent? order;
            try
            {
                order = partitionEvent.Data.EventBody
                    .ToObjectFromJson<OrderCreatedEvent>(
                        OrderCreatedEventJsonContext.Default.Options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Event deserialisation failed. " +
                    "PartitionId={PartitionId} Offset={Offset}",
                    partitionEvent.Partition.PartitionId,
                    partitionEvent.Data.OffsetString);
                // POST 12: Unlike Service Bus, there is no DeadLetterMessageAsync.
                // Failed events are not automatically moved anywhere.
                // Log the failure; the event remains in the partition.
                // Post 13 covers error handling with EventProcessorClient.
                continue;
            }

            activity?.SetTag(TelemetryConstants.AttrOrderId,  order.OrderId);
            activity?.SetTag(TelemetryConstants.AttrTenantId, order.TenantId);

            _logger.LogInformation(
                "Event Hubs: received order. " +
                "{OrderId} {TenantId} " +
                "PartitionId={PartitionId} Offset={Offset} " +
                "SequenceNumber={SequenceNumber}",
                order.OrderId, order.TenantId,
                partitionEvent.Partition.PartitionId,
                partitionEvent.Data.OffsetString,
                partitionEvent.Data.SequenceNumber);

            // POST 12: In production, checkpoint after every N events.
            // EventHubConsumerClient does not have built-in checkpoint storage.
            // EventProcessorClient (Post 13) handles this automatically via
            // Azurite blob storage.
        }
    }
}
