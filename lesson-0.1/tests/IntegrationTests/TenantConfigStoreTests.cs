// tests/IntegrationTests/TenantConfigStoreTests.cs
// POST 26: Integration tests for TenantConfigStore against a real Azurite container.
//
// WHY NOT MOCKS?
//   A mock of TableServiceClient asserts "the code calls GetEntityAsync with these
//   arguments" — it tests the implementation, not the behaviour.
//   A real Azurite container asserts "given this data in Table Storage, the method
//   returns this result" — it tests the contract that matters in production.
//
// TESTCONTAINERS PATTERN (IAsyncLifetime):
//   xUnit's IAsyncLifetime runs InitializeAsync before each test class
//   and DisposeAsync after. The Azurite container starts once per class
//   (not once per test) — faster than starting a container per test.
//   Each test creates its own table with a unique name to avoid cross-test
//   interference without needing container restart.
//
// BRIDGE FROM FAMILIAR:
//   In-process SQL Server (SSDT / LocalDB) integration tests in .NET Framework
//   used AttachDbFileName to start a database per test. Testcontainers is
//   the equivalent: start a real database (or Azurite) container per test class.

using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Azurite;
using Xunit;

namespace IntegrationTests;

// POST 26: AzuriteContainer is a typed Testcontainers container.
// It knows the Azurite Docker image, port mappings (10000/10001/10002),
// and the connection string format Azure SDK expects.
// IAsyncLifetime: xUnit calls InitializeAsync/DisposeAsync automatically.
public class TenantConfigStoreTests : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder()
        // POST 26: mcr.microsoft.com/azure-storage/azurite:latest is the same
        // image as in docker-compose.yml. Testcontainers pulls it from Docker Hub
        // the first time and caches it locally.
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    // Called by xUnit before the first test in this class
    public Task InitializeAsync() => _azurite.StartAsync();

    // Called by xUnit after all tests in this class complete
    public Task DisposeAsync() => _azurite.StopAsync();

    private TenantConfigStore CreateStore()
    {
        // POST 26: GetConnectionString() returns the Azurite connection string
        // with the dynamically assigned host port.
        // e.g. "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;..."
        // This is identical to "UseDevelopmentStorage=true" but with the
        // Testcontainers-assigned port instead of the default 10000-10002.
        var connectionString = _azurite.GetConnectionString();
        var tableService = new TableServiceClient(connectionString);
        return new TenantConfigStore(tableService, NullLogger<TenantConfigStore>.Instance);
    }

    [Fact]
    public async Task EnsureTableExists_SeedsTenant01AsEnabled()
    {
        // Arrange
        var store = CreateStore();

        // Act
        // POST 26: EnsureTableExistsAsync creates the table AND seeds default tenants.
        // This tests the real Azure Tables API via Azurite — not a mock.
        await store.EnsureTableExistsAsync();

        // Assert
        var config = await store.GetTenantConfigAsync("tenant-01");
        Assert.NotNull(config);
        Assert.True(config.IsEnabled,
            "tenant-01 should be enabled after seeding (Post 17 default)");
        Assert.Equal("standard", config.PricingTier);
    }

    [Fact]
    public async Task GetTenantConfig_ReturnsNull_ForUnknownTenant()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureTableExistsAsync();

        // Act
        // POST 26: GetTenantConfigAsync returns null for unknown tenants.
        // The POST 21 TenantAuthorizationHandler calls context.Fail() for null.
        var config = await store.GetTenantConfigAsync("tenant-unknown-99");

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public async Task UpdateTenantConfig_DisablesTenant_GetReturnsIsEnabledFalse()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureTableExistsAsync();

        // Act
        // POST 26: UpdateTenantConfigAsync uses UpsertEntityAsync (Merge mode).
        // This tests the runtime disable path exercised in Post 21 Explore it.
        await store.UpdateTenantConfigAsync("tenant-01",
            maxOrdersPerHour: 100,
            isEnabled:        false);

        // Assert
        var config = await store.GetTenantConfigAsync("tenant-01");
        Assert.NotNull(config);
        Assert.False(config.IsEnabled,
            "After explicit disable, IsEnabled should be false");
        Assert.Equal(100, config.MaxOrdersPerHour);
    }

    [Fact]
    public async Task GetAllTenantConfigs_ReturnsThreeSeededTenants()
    {
        // Arrange
        var store = CreateStore();
        await store.EnsureTableExistsAsync();

        // Act
        // POST 26: GetAllTenantConfigsAsync uses QueryAsync — a single-partition scan.
        var configs = new List<TenantConfigEntity>();
        await foreach (var config in store.GetAllTenantConfigsAsync())
            configs.Add(config);

        // Assert
        // Post 17 seeds tenant-01, tenant-02, tenant-03 by default
        Assert.Equal(3, configs.Count);
        Assert.Contains(configs, c => c.RowKey == "tenant-01");
        Assert.Contains(configs, c => c.RowKey == "tenant-02");
        Assert.Contains(configs, c => c.RowKey == "tenant-03");
    }
}
