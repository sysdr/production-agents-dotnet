// src/OrderProcessor/QueueDepthMonitor.cs
// POST 11: Background service that polls Service Bus queue runtime properties
// and exposes ActiveMessageCount and DeadLetterMessageCount as OTel gauges.
//
// These two metrics — visible in the Aspire Dashboard Metrics panel — are the
// monitoring signal that was missing in War Story #2. An alert on
// sb.orders.deadletter_messages > 0 would have fired within 30 seconds of
// the first dead-lettered message, not 6 hours later.
//
// Why ObservableGauge, not Counter or Histogram?
//   - Counter: monotonically increasing — wrong for queue depth (it goes down
//     as messages are processed).
//   - Histogram: for distributions (latency, payload size) — wrong here.
//   - ObservableGauge: current value, pulled by the OTel SDK on each
//     collection interval. Exactly right for queue depth — it reflects the
//     state at the moment of collection.
//
// The OTel SDK calls the lambda on each export interval (30s by default),
// so the polling loop and the gauge read are decoupled. The gauge reads
// whatever was last written by the polling loop.

using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

public class QueueDepthMonitor : BackgroundService
{
    private readonly ServiceBusAdministrationClient _adminClient;
    private readonly ILogger<QueueDepthMonitor> _logger;

    // Volatile: polling loop (background thread) writes, OTel SDK
    // observable gauge callback (collection thread) reads.
    private long _activeMessages    = 0;
    private long _dlqMessages       = 0;
    private long _scheduledMessages = 0;

    public QueueDepthMonitor(
        ServiceBusAdministrationClient adminClient,
        ILogger<QueueDepthMonitor> logger)
    {
        _adminClient = adminClient;
        _logger      = logger;

        // POST 11: Create OTel gauges. These appear in the Aspire Dashboard
        // Metrics panel as "sb.orders.active_messages", etc.
        // Meter name matches ServiceDefaults.Extensions.ActivitySourceName prefix.
        var meter = new Meter("HyperscaleLogMonitoring.ServiceBus", "1.0.0");

        meter.CreateObservableGauge<long>(
            name:        "sb.orders.active_messages",
            observeValue: () => Volatile.Read(ref _activeMessages),
            unit:        "messages",
            description: "Active messages in the orders queue — waiting to be processed");

        meter.CreateObservableGauge<long>(
            name:        "sb.orders.deadletter_messages",
            observeValue: () => Volatile.Read(ref _dlqMessages),
            unit:        "messages",
            description: "Dead-letter messages in the orders/$DeadLetterQueue — require investigation");

        meter.CreateObservableGauge<long>(
            name:        "sb.orders.scheduled_messages",
            observeValue: () => Volatile.Read(ref _scheduledMessages),
            unit:        "messages",
            description: "Scheduled (future-delivery) messages in the orders queue");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "QueueDepthMonitor started — polling orders queue every 30s");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // GET QUEUE RUNTIME PROPERTIES
                // ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync()
                // returns: ActiveMessageCount, ScheduledMessageCount,
                // DeadLetterMessageCount, TransferMessageCount, SizeInBytes,
                // CreatedAt, UpdatedAt, AccessedAt.
                //
                // This is the management API — not the data-plane API used
                // for sending/receiving messages. It requires RBAC role
                // "Azure Service Bus Data Owner" or the connection string's
                // SharedAccessKey to include management rights.
                var props = await _adminClient
                    .GetQueueRuntimePropertiesAsync("orders", stoppingToken);

                Volatile.Write(ref _activeMessages,    props.Value.ActiveMessageCount);
                Volatile.Write(ref _dlqMessages,       props.Value.DeadLetterMessageCount);
                Volatile.Write(ref _scheduledMessages, props.Value.ScheduledMessageCount);

                if (props.Value.DeadLetterMessageCount > 0)
                {
                    // POST 11: Log a warning when DLQ is non-empty.
                    // In production: this warning triggers an Azure Monitor alert.
                    // Locally: visible in Aspire Dashboard Structured Logs.
                    _logger.LogWarning(
                        "DLQ non-empty. {DeadLetterCount} messages in orders/$DeadLetterQueue. " +
                        "Active={ActiveCount} Scheduled={ScheduledCount}",
                        props.Value.DeadLetterMessageCount,
                        props.Value.ActiveMessageCount,
                        props.Value.ScheduledMessageCount);
                }
                else
                {
                    _logger.LogDebug(
                        "Queue depth: Active={ActiveCount} DLQ={DlqCount}",
                        props.Value.ActiveMessageCount,
                        props.Value.DeadLetterMessageCount);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to read queue runtime properties — " +
                    "will retry in 30s. Is the Service Bus Emulator running?");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        _logger.LogInformation("QueueDepthMonitor stopped");
    }
}
