// src/OrderEventProcessor/Program.cs
// CUMULATIVE — Post 13: upgrade from EventHubConsumerClient to EventProcessorClient
// with Azurite as the per-event checkpoint store.
//
// Post 12: EventHubConsumerClient + ReadEventsAsync — no checkpoint store,
//           starts from beginning on every restart (War Story #3 pattern)
// Post 13: EventProcessorClient + BlobContainerClient — per-event checkpoint
//           via Azurite blob storage (the War Story #3 fix)

using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var ehConnStr     = builder.Configuration["ConnectionStrings:eventhubs"]
                 ?? builder.Configuration["EventHubs:ConnectionString"];
var azuriteConnStr = builder.Configuration["ConnectionStrings:azurite"]
                  ?? builder.Configuration["Azurite:ConnectionString"]
                  ?? "UseDevelopmentStorage=true";  // Azurite default

if (!string.IsNullOrWhiteSpace(ehConnStr))
{
    // POST 13: BlobContainerClient — checkpoint storage backend.
    // Container "eh-checkpoints" in Azurite stores one blob per partition
    // with the last successfully processed Offset + SequenceNumber.
    // On restart, EventProcessorClient reads these blobs and resumes
    // from the last checkpoint — not from the beginning of the partition.
    //
    // Production: replace Azurite connection string with Azure Storage account.
    // One line change. The EventProcessorClient API does not change.
    builder.Services.AddSingleton(_ =>
    {
        var blobClient = new BlobContainerClient(azuriteConnStr, "eh-checkpoints");
        blobClient.CreateIfNotExists();   // create container if first run
        return blobClient;
    });

    // POST 13: EventProcessorClient — the production-grade replacement for
    // EventHubConsumerClient.ReadEventsAsync().
    //
    // Key differences from EventHubConsumerClient:
    //   - Manages partition ownership across multiple processor instances
    //   - Calls ProcessEventAsync once per partition (not interleaved)
    //   - Integrates with a checkpoint store for restart position
    //   - Handles partition rebalancing automatically
    builder.Services.AddSingleton(provider =>
    {
        var blobClient = provider.GetRequiredService<BlobContainerClient>();
        return new EventProcessorClient(
            blobClient,
            "order-processor",   // use the dedicated consumer group (not $Default)
            ehConnStr,
            "order-events",
            new EventProcessorClientOptions
            {
                // MaximumWaitTime: how long the processor waits for events before
                // calling ProcessEventAsync with an empty event (for heartbeat).
                // Set to null to only call ProcessEventAsync when there are events.
                MaximumWaitTime         = TimeSpan.FromSeconds(10),
                // LoadBalancingStrategy: how partitions are distributed across
                // multiple processor instances. Balanced = even distribution.
                LoadBalancingStrategy   = LoadBalancingStrategy.Balanced,
                // PartitionOwnershipExpirationInterval: how long before another
                // processor can claim a partition if this one is unresponsive.
                PartitionOwnershipExpirationInterval = TimeSpan.FromMinutes(1),
            });
    });

    builder.Services.AddHostedService<OrderEventProcessorWorker>();
}

var host = builder.Build();
// POST 25: Note on Dapr pub/sub in Worker Services.
// This service is a BackgroundService (IHostedService) — it uses host.Run(), not app.Run().
// For Dapr pub/sub in a Worker Service, use DaprClient.CreateSubscription() in the worker
// or run the subscription handler in a separate ASP.NET Core endpoint if needed.
// The subscriber endpoint demonstrated in this post is in OrderEventProcessor/Subscriber.cs (see below).
// For the series, the direct EH EventProcessorClient (Post 12-13) remains the primary consumer.
// Dapr pub/sub is added as a parallel path for Post 25 demonstration.
host.Run();
