// src/OrderProcessor/Program.cs
// CUMULATIVE — Post 11: add QueueDepthMonitor for DLQ depth monitoring.
//
// Post 8:  ServiceBusProcessor with MaxConcurrentCalls
// Post 9:  poison message detection + DLQ explicit dead-lettering
// Post 10: ResiliencePipeline (retry + timeout)
// Post 11: QueueDepthMonitor — polls queue runtime properties + OTel gauges NEW

using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connStr = builder.Configuration["ConnectionStrings:servicebus"]
           ?? builder.Configuration["ServiceBus:ConnectionString"];

if (!string.IsNullOrWhiteSpace(connStr))
{
    // Data-plane client — for sending/receiving messages
    builder.Services.AddSingleton(_ => new ServiceBusClient(connStr));

    // POST 11: Management-plane client — for queue runtime properties.
    // Same connection string; different SDK surface area.
    // Requires: SharedAccessKeyName with Manage rights, or
    //           RBAC: "Azure Service Bus Data Owner" role assignment.
    builder.Services.AddSingleton(_ =>
        new ServiceBusAdministrationClient(connStr));

    builder.Services.AddSingleton(provider =>
    {
        var client = provider.GetRequiredService<ServiceBusClient>();
        return client.CreateProcessor("orders", new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls           = 4,
            AutoCompleteMessages         = false,
            MaxAutoLockRenewalDuration   = TimeSpan.FromMinutes(5),
            ReceiveMode                  = ServiceBusReceiveMode.PeekLock,
        });
    });

    builder.Services.AddHostedService<OrderProcessorWorker>();

    // POST 11: Register QueueDepthMonitor as a second BackgroundService.
    // Runs concurrently with OrderProcessorWorker. Polls every 30s.
    builder.Services.AddHostedService<QueueDepthMonitor>();
}

// POST 11: Register the custom OTel meter so the Aspire Dashboard
// picks up sb.orders.active_messages and sb.orders.deadletter_messages.
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
        metrics.AddMeter("HyperscaleLogMonitoring.ServiceBus"));

// POST 16: Azure Blob Storage client (Azurite locally, Azure Storage in production).
// Shared BlobServiceClient — thread-safe singleton, manages HTTP connection pool.
// Same pattern as ServiceBusClient (Post 7) and EventProcessorClient (Post 13).
var azuriteConnStr = builder.Configuration["ConnectionStrings:azurite"]
                  ?? builder.Configuration["Azurite:ConnectionString"]
                  ?? "UseDevelopmentStorage=true";

var blobService = new Azure.Storage.Blobs.BlobServiceClient(azuriteConnStr);
builder.Services.AddSingleton(blobService);
builder.Services.AddSingleton<OrderBlobStore>();

// Create the container at startup (idempotent)
var tempHost = builder.Build();
var blobStore = tempHost.Services.GetRequiredService<OrderBlobStore>();
await blobStore.EnsureContainerExistsAsync();

var host = tempHost;
host.Run();
