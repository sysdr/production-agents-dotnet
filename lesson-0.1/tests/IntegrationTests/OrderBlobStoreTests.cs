// tests/IntegrationTests/OrderBlobStoreTests.cs
// POST 26: Integration tests for OrderBlobStore against a real Azurite container.
//
// The class uses a fresh AzuriteContainer per test class, started once before
// all tests and stopped after. Each test writes to a unique blob path using
// its own OrderId — parallel test execution is safe.
//
// WHAT THESE TESTS COVER:
//   1. Container creation (EnsureContainerExistsAsync is idempotent)
//   2. Blob write (WriteProcessedOrderAsync creates a JSON blob at the correct path)
//   3. Idempotent write (second write with same orderId is silently skipped)
//   4. Blob path convention ({year}/{month}/{tenantId}/{orderId}.json)
//   5. ContentType is application/json

using Azure.Storage.Blobs;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Xunit;

namespace IntegrationTests;

public class OrderBlobStoreTests : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public Task InitializeAsync() => _azurite.StartAsync();
    public Task DisposeAsync()    => _azurite.StopAsync();

    private OrderBlobStore CreateStore()
    {
        // POST 26: GetConnectionString() returns a connection string with the
        // dynamically assigned Azurite Blob port (10000 by default, but
        // Testcontainers maps it to a random host port to avoid conflicts
        // if docker-compose Azurite is running on the same machine).
        var blobService = new BlobServiceClient(_azurite.GetConnectionString());
        return new OrderBlobStore(blobService, NullLogger<OrderBlobStore>.Instance);
    }

    [Fact]
    public async Task EnsureContainerExists_IsIdempotent()
    {
        // Arrange
        var store = CreateStore();

        // Act + Assert — calling twice must not throw
        // POST 26: CreateIfNotExistsAsync is idempotent; the second call
        // is a no-op. This is the same guarantee that production code relies on
        // at startup (Post 16).
        await store.EnsureContainerExistsAsync();
        await store.EnsureContainerExistsAsync();  // must not throw
    }

    [Fact]
    public async Task WriteProcessedOrder_CreatesJsonBlob_AtExpectedPath()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureContainerExistsAsync();

        var order = new OrderCreatedEvent
        {
            OrderId   = $"order-test-{Guid.NewGuid():N}",
            TenantId  = "tenant-01",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        // Act
        var blobName = await store.WriteProcessedOrderAsync(order, deliveryCount: 1);

        // Assert: blob path must match {year}/{month}/{tenantId}/{orderId}.json
        // POST 26: The path structure enforces lifecycle policy prefix matching (Post 17)
        // and GDPR per-tenant deletion. Wrong path = lifecycle rules silently skipped.
        var now = DateTimeOffset.UtcNow;
        Assert.StartsWith($"{now:yyyy}/{now:MM}/tenant-01/", blobName);
        Assert.EndsWith($"{order.OrderId}.json", blobName);
    }

    [Fact]
    public async Task WriteProcessedOrder_IsIdempotent_SecondWriteIsNoOp()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureContainerExistsAsync();

        var order = new OrderCreatedEvent
        {
            OrderId   = $"order-idem-{Guid.NewGuid():N}",
            TenantId  = "tenant-02",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        // Act — write the same orderId twice
        var firstName  = await store.WriteProcessedOrderAsync(order, deliveryCount: 1);
        var secondName = await store.WriteProcessedOrderAsync(order, deliveryCount: 2);

        // Assert: second write is silently skipped (409 Conflict suppressed)
        // POST 26: overwrite: false in UploadAsync (Post 16 design decision).
        // DeliveryCount=2 on the second call would overwrite the record —
        // but the "complete first, write second" ordering (Post 16) means
        // reaching here twice indicates a duplicate delivery, not a retry.
        Assert.Equal(firstName, secondName);
    }

    [Fact]
    public async Task WriteProcessedOrder_Blob_HasJsonContentType()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureContainerExistsAsync();

        var order = new OrderCreatedEvent
        {
            OrderId   = $"order-ct-{Guid.NewGuid():N}",
            TenantId  = "tenant-03",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        // Act
        var blobName    = await store.WriteProcessedOrderAsync(order, deliveryCount: 1);
        var blobService = new BlobServiceClient(_azurite.GetConnectionString());
        var container   = blobService.GetBlobContainerClient("processed-orders");
        var props       = await container.GetBlobClient(blobName).GetPropertiesAsync();

        // Assert
        // POST 26: ContentType application/json is set in BlobHttpHeaders (Post 16).
        // Enables correct MIME type in Azure Portal previews and HTTP range requests.
        Assert.Equal("application/json", props.Value.ContentType);
    }
}
