// src/OrderService/TenantConfigStore.cs
// POST 17: Per-tenant configuration stored in Azure Table Storage (Azurite locally).
//
// WHY TABLE STORAGE instead of appsettings.json or database?
//   - appsettings.json: config is per-deployment, not per-tenant.
//     Changing one tenant's config requires a deployment.
//   - SQL Server: config reads on every request hit a relational database,
//     which is over-engineered for key-value config lookups.
//   - Table Storage: O(1) lookup by PartitionKey + RowKey, no schema
//     migrations, no connection pooling overhead for simple reads.
//     Config changes take effect immediately without redeployment.
//
// TABLE DESIGN:
//   Table:        tenant-config
//   PartitionKey: "config"          — all tenant config in one partition
//                                     (keeps related rows on the same storage node)
//   RowKey:       tenantId          — unique per tenant
//   Properties:   MaxOrdersPerHour, IsEnabled, PricingTier, Notes
//
// PartitionKey = "config" means all TenantConfig rows are co-located.
// A scan query "give me all tenant configs" hits one partition = one RU.
// Compare to PartitionKey = tenantId: each tenant is in a different
// partition — efficient per-tenant lookup but expensive cross-tenant scan.
//
// POST 17 principle: design the PartitionKey for your most common query.
// If "get one tenant's config" is 99% of queries → PartitionKey = tenantId.
// If "list all tenants' configs" is common → PartitionKey = "config".
// This system lists all tenants occasionally (admin page) and reads one
// tenant frequently (per order) → PartitionKey = "config" is the right choice.

using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

// POST 17: ITableEntity is the base interface for Table Storage entities.
// Azure.Data.Tables 12.x uses a record-based approach — implement ITableEntity
// or use TableEntity (a dictionary-based alternative).
// The typed approach (implementing ITableEntity) gives compile-time safety.
public class TenantConfigEntity : ITableEntity
{
    // POST 17: PartitionKey and RowKey are required — they form the composite key.
    // ETag and Timestamp are managed by Table Storage for optimistic concurrency.
    public string  PartitionKey { get; set; } = "config";
    public string  RowKey       { get; set; } = string.Empty;   // = tenantId
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    // Business properties — any serialisable type
    public int    MaxOrdersPerHour { get; set; } = 1000;
    public bool   IsEnabled        { get; set; } = true;
    public string PricingTier      { get; set; } = "standard";
    public string Notes            { get; set; } = string.Empty;
}

public class TenantConfigStore
{
    private readonly TableClient _tableClient;
    private readonly ILogger<TenantConfigStore> _logger;

    private const string TableName = "tenantconfig";   // lowercase: Table names are case-insensitive

    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    public TenantConfigStore(TableServiceClient tableService,
        ILogger<TenantConfigStore> logger,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _tableClient = tableService.GetTableClient(TableName);
        _logger      = logger;
        _cache       = cache;  // POST 28: null in tests (NullLogger pattern)
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default)
    {
        // POST 17: CreateIfNotExistsAsync is idempotent — same pattern as
        // BlobContainerClient.CreateIfNotExistsAsync (Post 16).
        await _tableClient.CreateIfNotExistsAsync(ct);
        _logger.LogInformation("Azure Table '{Table}' ready (Azurite port 10002)", TableName);

        // Seed default configs for the three tenants used in the series
        await SeedDefaultTenantsAsync(ct);
    }

    private async Task SeedDefaultTenantsAsync(CancellationToken ct)
    {
        var defaultTenants = new[]
        {
            new TenantConfigEntity { RowKey = "tenant-01", MaxOrdersPerHour = 1000, IsEnabled = true, PricingTier = "standard" },
            new TenantConfigEntity { RowKey = "tenant-02", MaxOrdersPerHour = 500,  IsEnabled = true, PricingTier = "basic"    },
            new TenantConfigEntity { RowKey = "tenant-03", MaxOrdersPerHour = 2000, IsEnabled = true, PricingTier = "premium"  },
        };

        foreach (var tenant in defaultTenants)
        {
            // UpsertEntityAsync: insert or replace — idempotent seed
            await _tableClient.UpsertEntityAsync(tenant, TableUpdateMode.Replace, ct);
        }

        _logger.LogInformation(
            "Seeded tenant configs: tenant-01 (standard 1000/hr), " +
            "tenant-02 (basic 500/hr), tenant-03 (premium 2000/hr)");
    }

    // GET: retrieve config for one tenant — O(1) by PartitionKey + RowKey
    public async Task<TenantConfigEntity?> GetTenantConfigAsync(
        string tenantId, CancellationToken ct = default)
    {
        // POST 28: check in-memory cache before hitting Table Storage.
        // At 200 RPS, this eliminates 200 Table Storage round trips per second.
        // Cache key is per-tenant so different tenants do not share entries.
        if (_cache?.TryGetValue($"tenant:{tenantId}", out var cachedObj) == true
            && cachedObj is TenantConfigEntity cached)
            return cached;

        try
        {
            var response = await _tableClient.GetEntityAsync<TenantConfigEntity>(
                partitionKey: "config",
                rowKey:       tenantId,
                cancellationToken: ct);

            // POST 28: cache for 30 seconds.
            // A tenant disabled via UpdateTenantConfigAsync (Post 21) takes up to
            // 30 seconds to take effect. Acceptable trade-off for the throughput gain.
            _cache?.Set($"tenant:{tenantId}", response.Value,
                TimeSpan.FromSeconds(30));

            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("No config for {TenantId} — using defaults", tenantId);
            return null;
        }
    }

    // LIST: all tenant configs — single-partition scan
    public async IAsyncEnumerable<TenantConfigEntity> GetAllTenantConfigsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // POST 17: QueryAsync<T>(filter) with a PartitionKey filter is efficient —
        // it scans only the "config" partition, not the entire table.
        // Equivalent to: SELECT * FROM tenantconfig WHERE PartitionKey = 'config'
        await foreach (var entity in _tableClient
            .QueryAsync<TenantConfigEntity>(
                filter: "PartitionKey eq 'config'",
                cancellationToken: ct))
        {
            yield return entity;
        }
    }

    // UPDATE: change a tenant's rate limit or enabled state at runtime
    public async Task UpdateTenantConfigAsync(
        string tenantId, int maxOrdersPerHour, bool isEnabled, CancellationToken ct = default)
    {
        // POST 17: UpsertEntityAsync with Replace mode: insert if not exists,
        // replace entirely if exists. For partial updates, use MergeAsync or
        // UpdateEntityAsync with TableUpdateMode.Merge.
        var entity = new TenantConfigEntity
        {
            RowKey           = tenantId,
            MaxOrdersPerHour = maxOrdersPerHour,
            IsEnabled        = isEnabled,
            PricingTier      = "custom",
        };

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct);
        _logger.LogInformation(
            "Tenant config updated. {TenantId} MaxOrders={MaxOrders} Enabled={Enabled}",
            tenantId, maxOrdersPerHour, isEnabled);
    }
}
