// src/OrderProcessor/OrderBlobStore.cs
// POST 16: Writes processed order records to Azure Blob Storage (Azurite locally).
//
// Called by OrderProcessorWorker after CompleteMessageAsync succeeds.
// The order record persists beyond the Service Bus message lifetime.
//
// BLOB PATH STRUCTURE: processed-orders/{year}/{month}/{tenantId}/{orderId}.json
//
//   processed-orders/
//     2024/
//       07/
//         tenant-01/
//           order-abc123.json
//         tenant-03/
//           order-def456.json
//
// WHY THIS PATH STRUCTURE?
//
//   {year}/{month}/     — lifecycle policy prefix.
//                         An Azure Blob lifecycle rule can match
//                         "processed-orders/2023/" to tier old blobs to
//                         archive storage automatically (Post 17 covers this).
//
//   {tenantId}/         — per-tenant isolation.
//                         GDPR right-to-erasure: delete all blobs under
//                         "processed-orders/{year}/{month}/{tenantId}/"
//                         to remove one tenant's data without affecting others.
//
//   {orderId}.json      — unique blob name.
//                         If the same orderId is processed twice (edge case
//                         despite RequiresDuplicateDetection), the second
//                         write fails silently (overwrite: false).
//                         The blob already contains a successful record.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public class OrderBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<OrderBlobStore> _logger;

    private const string ContainerName = "processed-orders";

    public OrderBlobStore(BlobServiceClient blobService, ILogger<OrderBlobStore> logger)
    {
        // GetBlobContainerClient does not make a network call — it is a local object.
        // CreateIfNotExistsAsync (below) makes the network call.
        _container = blobService.GetBlobContainerClient(ContainerName);
        _logger    = logger;
    }

    // Called once at startup to ensure the container exists.
    // BlobContainerClient.CreateIfNotExistsAsync is idempotent —
    // it succeeds whether or not the container already exists.
    public async Task EnsureContainerExistsAsync(CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(
            publicAccessType: PublicAccessType.None,   // private by default
            cancellationToken: ct);

        _logger.LogInformation(
            "Azurite container '{Container}' ready", ContainerName);
    }

    // Writes a processed order record as a JSON blob.
    // Returns the blob name (for logging and tracing).
    public async Task<string> WriteProcessedOrderAsync(
        OrderCreatedEvent order,
        int deliveryCount,
        CancellationToken ct = default)
    {
        // POST 16: Date-partitioned path ensures lifecycle policy rules work.
        // Azurite does not enforce lifecycle policies (that is an Azure service feature),
        // but the path structure is production-ready so the same path works after
        // deployment with no code change.
        var now = DateTimeOffset.UtcNow;
        var blobName = $"{now:yyyy}/{now:MM}/{order.TenantId}/{order.OrderId}.json";

        var blobClient = _container.GetBlobClient(blobName);

        // Serialise the order record with processing metadata
        var record = new ProcessedOrderRecord(
            OrderId:       order.OrderId,
            TenantId:      order.TenantId,
            CreatedAt:     order.CreatedAt,
            ProcessedAt:   now.ToString("O"),
            DeliveryCount: deliveryCount);

        // LESSON 0.1 (.NET 10): the old code probed _jsonOptions.GetConverter(...) and
        // fell back to reflection. A source-generated context now throws
        // NotSupportedException for unknown types instead of returning null, so
        // ProcessedOrderRecord gets its own source-generated context (below).
        var json = JsonSerializer.Serialize(record, ProcessedOrderJsonContext.Default.ProcessedOrderRecord);

        var content = BinaryData.FromString(json);

        // POST 16: overwrite: false — if the blob already exists, this order was
        // already processed successfully. The existing record is authoritative.
        // RequestFailedException with status 409 (Conflict) means already exists.
        try
        {
            await blobClient.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    HttpHeaders   = new BlobHttpHeaders { ContentType = "application/json" },
                    // POST 16: Blob metadata — queryable without downloading the blob.
                    // Azure Blob Storage supports listing blobs and filtering by metadata.
                    // Useful for "show all orders for tenant-03 with DeliveryCount > 1."
                    Metadata = new Dictionary<string, string>
                    {
                        ["OrderId"]       = order.OrderId,
                        ["TenantId"]      = order.TenantId,
                        ["DeliveryCount"] = deliveryCount.ToString(),
                        ["ProcessedAt"]   = now.ToString("O"),
                    },
                    // Fail if blob already exists (If-None-Match: *) — duplicate delivery → 409
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                },
                ct);

            _logger.LogInformation(
                "Blob written. {OrderId} {TenantId} Blob={BlobName}",
                order.OrderId, order.TenantId, blobName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            // Already exists — this order was already processed.
            // Log at debug level (not warning) — this is expected for duplicate deliveries.
            _logger.LogDebug(
                "Blob already exists — duplicate delivery skipped. {OrderId} Blob={BlobName}",
                order.OrderId, blobName);
        }

        return blobName;
    }
}

// POST 16: The processed order record persisted to Blob Storage.
// Separate from OrderCreatedEvent (the command) — this is the result.
// Contains processing metadata (ProcessedAt, DeliveryCount) not in the original event.
public record ProcessedOrderRecord(
    string OrderId,
    string TenantId,
    string CreatedAt,
    string ProcessedAt,
    int    DeliveryCount);

// LESSON 0.1: source-generated (trim and AOT safe) metadata for the blob record.
[System.Text.Json.Serialization.JsonSerializable(typeof(ProcessedOrderRecord))]
public partial class ProcessedOrderJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
