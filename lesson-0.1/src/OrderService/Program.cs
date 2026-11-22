// src/OrderService/Program.cs
// CUMULATIVE — Post 7: send OrderCreatedEvent to Service Bus via BinaryData.
//
// Post 1: Minimal API + OTel + health checks
// Post 2: Serilog enrichers (via ServiceDefaults)
// Post 3: OTel SemanticConventions + ValidateCredentials span
// Post 4: multi-span /orders/create + HttpClient dependency span
// Post 5: TelemetryConstants constants
// Post 7: OrderCreatedEvent published to Service Bus "orders" queue NEW

using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OrderService;
using ServiceDefaults;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// POST 7: Register ServiceBusClient as a singleton.
// Connection string comes from appsettings.json or environment variable.
// In Aspire AppHost, it is injected automatically via WithReference().
// In docker-compose, it is set in the environment block.
//
// WHY singleton: ServiceBusClient manages TCP connections and AMQP sessions.
// Creating a new client per request is expensive and causes connection pool
// exhaustion under load. One client per service process, many senders/receivers.
var sbConnectionString = builder.Configuration["ConnectionStrings:servicebus"]
                      ?? builder.Configuration["ServiceBus:ConnectionString"];

ServiceBusClient? serviceBusClient = null;
if (!string.IsNullOrWhiteSpace(sbConnectionString))
{
    serviceBusClient = new ServiceBusClient(sbConnectionString);
    builder.Services.AddSingleton(serviceBusClient);
    builder.Services.AddSingleton(_ => serviceBusClient.CreateSender("orders"));
}

// POST 12: Register EventHubProducerClient as a singleton.
// Connects to "order-events" Event Hub on the Event Hubs Emulator.
// The emulator runs on host port 5673 (mapped from container port 5672)
// to avoid conflicting with Service Bus Emulator on port 5672.
//
// WHY dual-publish to both Service Bus and Event Hubs?
//   Service Bus: order-processor consumes, processes exactly once, DLQ on failure.
//   Event Hubs:  multiple independent consumers (audit-log, analytics, etc.)
//     each read the full event stream. Service Bus is for commands;
//     Event Hubs is for events that multiple systems need to observe.
var ehConnectionString = builder.Configuration["ConnectionStrings:eventhubs"]
                      ?? builder.Configuration["EventHubs:ConnectionString"];

Azure.Messaging.EventHubs.Producer.EventHubProducerClient? ehProducer = null;
if (!string.IsNullOrWhiteSpace(ehConnectionString))
{
    ehProducer = new Azure.Messaging.EventHubs.Producer.EventHubProducerClient(
        ehConnectionString, "order-events");
    builder.Services.AddSingleton(ehProducer);
}

// POST 20: Register JWT bearer authentication.
// Local dev: uses symmetric key from appsettings.Development.json Jwt section.
// Production: replace with builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration).
builder.Services.AddJwtAuth(builder.Configuration);

// POST 24: Register DaprClient as a singleton for service invocation.
// Communicates with the local Dapr sidecar on localhost:3500.
builder.Services.AddDaprClient();

// POST 35: Register the Orders.Business meter so OTel SDK picks it up.
// Without this line, BusinessMetrics.Meter instruments are created but never
// collected — the measurements disappear silently (the observability gap in code form).
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(TelemetryConstants.MeterOrdersBusiness));

// POST 35: Register the business meter — without this, BusinessMetrics instruments
// are created but never collected. Silent data loss: OTel Collector receives nothing.
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(TelemetryConstants.MeterOrdersBusiness));

// POST 28: Register IMemoryCache — used by TenantConfigStore to cache per-tenant
// Table Storage lookups. Reduces Table Storage calls from 200/s to 1/30s at 200 RPS.
// In-process cache only — no Redis needed. Each OrderService replica has its own cache.
// Acceptable for tenant config (rarely changes); not appropriate for order state.
builder.Services.AddMemoryCache();

// POST 17/28: Azurite Table Storage + TenantConfigStore for JWT tenant authorization.
var azuriteTables = builder.Configuration["ConnectionStrings:azurite"]
                 ?? "UseDevelopmentStorage=true";
builder.Services.AddSingleton(_ => new TableServiceClient(azuriteTables));
builder.Services.AddSingleton<TenantConfigStore>();

var app = builder.Build();

// Seed tenant-01/02/03 so RequireEnabledTenant can authorize local JWTs.
try
{
    await app.Services.GetRequiredService<TenantConfigStore>().EnsureTableExistsAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "TenantConfigStore seed skipped — Azurite may still be starting");
}

app.UseAuthentication();   // POST 20: must come before UseAuthorization and MapDefaultEndpoints
app.UseAuthorization();
app.MapDefaultEndpoints();

var activitySource = Extensions.ActivitySource;

app.MapGet("/orders/ping", () => Results.Ok("pong"));

// POST 20: [Authorize] — /orders/create now requires a valid JWT bearer token.
// Request without Authorization header → 401 Unauthorized.
// Request with expired/invalid token → 401 Unauthorized.
// Request with valid token → tenant_id claim extracted in AuthConfig.OnTokenValidated.
// /orders/ping, /orders/metrics, and /health remain open (no RequireAuthorization).
app.MapPost("/orders/create", async (
    ILogger<Program> logger,
    HttpContext httpContext) =>
{
    // Optional: null when ConnectionStrings:servicebus / eventhubs are not configured.
    var sender = httpContext.RequestServices.GetService<ServiceBusSender>();
    var ehClient = httpContext.RequestServices
        .GetService<Azure.Messaging.EventHubs.Producer.EventHubProducerClient>()
        ?? ehProducer;
    var orderId  = $"order-{Random.Shared.Next(10000, 99999)}";
    // Prefer tenant from JWT; fall back to random for unauthenticated probes.
    var tenantId = httpContext.User?.FindFirst("tenant_id")?.Value
                ?? $"tenant-{Random.Shared.Next(1, 4):D2}";

    using var createSpan = activitySource.StartActivity("CreateOrder");
    createSpan?.SetTag(TelemetryConstants.AttrOrderId,  orderId);
    createSpan?.SetTag(TelemetryConstants.AttrTenantId, tenantId);

    logger.LogInformation(
        "Order creation started. {OrderId} {TenantId}",
        orderId, tenantId);

    await Task.Delay(Random.Shared.Next(20, 80));

    // POST 7: Build the domain event and serialise it to the wire.
    var orderEvent = new OrderCreatedEvent
    {
        OrderId    = orderId,
        TenantId   = tenantId,
        CreatedAt  = DateTimeOffset.UtcNow.ToString("O"),
        AmountCents= Random.Shared.Next(500, 50000),
        Currency   = "USD",
    };

    using var publishSpan = activitySource.StartActivity("PublishOrderEvent");
    publishSpan?.SetTag(TelemetryConstants.AttrEventType, "OrderCreated");
    publishSpan?.SetTag(TelemetryConstants.AttrTenantId,   tenantId);

    // Shared body for Service Bus + Event Hubs dual-publish (Post 12).
    var body = BinaryData.FromObjectAsJson(
        orderEvent,
        OrderCreatedEventJsonContext.Default.Options);

    if (sender is not null)
    {
        // POST 7: BinaryData.FromObjectAsJson<T> with source-generated context.
        // This is the preferred Azure SDK pattern:
        //   - Source generator context → AOT-safe, no reflection
        //   - BinaryData → Azure SDK's universal message body type
        //   - ContentType header → helps subscribers skip deserialisation
        //
        // NOT this (reflection-based, not AOT-safe):
        //   BinaryData.FromObjectAsJson(orderEvent)   ← uses default JsonSerializer
        //
        // BUT this (source-generated, AOT-safe):
        //   BinaryData.FromObjectAsJson(orderEvent, OrderCreatedEventJsonContext.Default.Options)
        var message = new ServiceBusMessage(body)
        {
            ContentType            = "application/json",
            Subject                = "OrderCreated",
            // TenantId as application property — used for SQL filter routing (Post 25)
            ApplicationProperties  = { [TelemetryConstants.AttrTenantId] = tenantId },
            // MessageId prevents duplicate processing if the publisher retries
            MessageId              = orderId,
        };

        // POST 39: Inject W3C traceparent into ApplicationProperties.
        // Without this, OrderProcessor's span starts a disconnected new trace —
        // the ADX "full order trace" query would only ever find one hop.
        message.ApplicationProperties.InjectTraceContext(publishSpan);

        await sender.SendMessageAsync(message);

        // Measure the wire payload size — visible in the log record
        logger.LogInformation(
            "OrderCreated event sent. {OrderId} {TenantId} {PayloadBytes}",
            orderId, tenantId, body.ToMemory().Length);
    }
    else
    {
        logger.LogWarning(
            "Service Bus not configured — OrderCreated event not published. {OrderId}",
            orderId);
    }

    // POST 12: Also publish to Event Hubs for streaming consumers.
    // Service Bus = command (exactly-once, DLQ on failure).
    // Event Hubs  = event (fan-out, multiple consumer groups, retained).
    if (ehClient is not null)
    {
        var ehEvent = new Azure.Messaging.EventHubs.EventData(body)
        {
            ContentType = "application/json",
            MessageId   = orderId,
            // PartitionKey routes correlated events to the same partition.
            // TenantId ensures all events for one tenant land in the same partition
            // (and thus arrive in order for that tenant). Post 13 uses this for
            // TenantId-aware consumer groups.
        };

        // POST 39: Same traceparent injection as the Service Bus path above.
        // EventData.Properties is EH's equivalent of ApplicationProperties.
        // This means BOTH downstream paths (Service Bus → OrderProcessor,
        // Event Hubs → OrderEventProcessor) are children of the same publishSpan,
        // and both appear under the same TraceId in ADX.
        ehEvent.Properties.InjectTraceContext(publishSpan);
        // Use a batch to efficiently group multiple events (here: one event)
        using var batch = await ehClient.CreateBatchAsync(
            new Azure.Messaging.EventHubs.Producer.CreateBatchOptions
            {
                PartitionKey = tenantId   // route by TenantId
            });
        batch.TryAdd(ehEvent);
        await ehClient.SendAsync(batch);

        logger.LogInformation(
            "OrderCreated event published to Event Hubs. {OrderId} {TenantId} PartitionKey={TenantId}",
            orderId, tenantId, tenantId);
    }

    // POST 35: Record business metrics — fills the observability gap.
    // Without these two lines, ADX queries for revenue and order counts return 0 rows.
    // The random value simulates order dollar amount — replace with request.Total in production.
    var orderValue = Math.Round(Random.Shared.NextDouble() * 490 + 10, 2); // $10-$500
    BusinessMetrics.OrderValue.Record(orderValue,
        new System.Collections.Generic.KeyValuePair<string, object?>(
            TelemetryConstants.AttrTenantId, tenantId));
    BusinessMetrics.TenantOrderCount.Add(1,
        new System.Collections.Generic.KeyValuePair<string, object?>(
            TelemetryConstants.AttrTenantId, tenantId));

    return Results.Created($"/orders/{orderId}",
        new { OrderId = orderId, TenantId = tenantId, Status = "created",
              Value = orderValue });
// POST 20: RequireAuthorization() upgraded to named policy in Post 21.
// "RequireEnabledTenant" = valid JWT (Post 20) AND tenant_id claim (Post 20)
//   AND tenant exists in TenantConfigStore (Post 17) AND IsEnabled = true.
// Returns 403 Forbidden (not 401 Unauthorized) when tenant is disabled —
// because the JWT is valid; only the business rule denies the request.
}).RequireAuthorization("RequireEnabledTenant");

app.MapDelete("/orders/{id}", (string id, ILogger<Program> logger) =>
{
    logger.LogWarning("Cancel requested. {OrderId}", id);
    logger.LogError("Cancel blocked — payment processed. {OrderId}", id);
    return Results.Conflict(new { Error = "Cannot cancel paid order" });
});

// POST 18: /orders/metrics — generates per-tenant statistics as structured OTel spans.
// These appear in App Insights `customEvents` table when the Azure Monitor OTel Distro
// maps OTel spans with names beginning "metrics." to customEvents.
// Also visible in Aspire Dashboard Traces panel as "metrics.tenantStats" spans.
//
// Purpose for Post 18: provides real data for the customEvents KQL queries in
// docs/kql-queries.md. Run this endpoint a few times, then query:
//   customEvents | where name has "tenantStats"
//   | project timestamp, TenantId=tostring(customDimensions.TenantId), Count=toint(customDimensions.OrderCount)
app.MapGet("/orders/metrics", async (
    ILogger<Program> logger,
    [FromServices] TableServiceClient tableService) =>
{
    using var activity = Extensions.ActivitySource.StartActivity("metrics.tenantStats");

    // Simulate per-tenant order statistics (in production, read from Blob or Table)
    var tenantStats = new[]
    {
        new { TenantId = "tenant-01", OrderCount = Random.Shared.Next(100, 500), PricingTier = "standard" },
        new { TenantId = "tenant-02", OrderCount = Random.Shared.Next(50,  200), PricingTier = "basic"    },
        new { TenantId = "tenant-03", OrderCount = Random.Shared.Next(200, 800), PricingTier = "premium"  },
    };

    foreach (var stat in tenantStats)
    {
        // POST 18: Structured log with ordered count — appears in App Insights traces
        // table (severityLevel=1) with TenantId and OrderCount in customDimensions.
        // Use in KQL: traces | where message has "Tenant stats" | project ...
        logger.LogInformation(
            "Tenant stats. {TenantId} {PricingTier} {OrderCount} orders this hour",
            stat.TenantId, stat.PricingTier, stat.OrderCount);
    }

    activity?.SetTag("tenants.count", tenantStats.Length);
    return Results.Ok(tenantStats);
});

// POST 24: Dapr service invocation demo endpoint
app.MapGet("/orders/dapr-invoke", async (
    Dapr.Client.DaprClient daprClient,
    ILogger<Program> logger) =>
{
    using var activity = Extensions.ActivitySource.StartActivity("DaprServiceInvoke");
    try
    {
        var status = await daprClient.InvokeMethodAsync<UserServiceStatus>(
            HttpMethod.Get, "user-service", "users/status");
        activity?.SetTag("dapr.target_app_id", "user-service");
        activity?.SetTag("dapr.status", status.Status);
        logger.LogInformation("Dapr service invocation succeeded. {TargetService} {Status}",
            "user-service", status.Status);
        return Results.Ok(new { Source="order-service", Target="user-service",
            Method="Dapr service invocation", Status=status.Status });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Dapr service invocation failed.");
        return Results.Problem(title: "Dapr invocation failed", detail: ex.Message);
    }
});

// POST 25: Dapr pub/sub publish endpoint.
// Publishes OrderCreatedEvent to the "order-pubsub" component via DaprClient.
// The component config (config/dapr/components/pubsub.yaml) determines the
// backing broker — Redis locally, Azure Service Bus in production.
// Application code: identical regardless of broker. Only the YAML changes.
//
// COMPARE TO DIRECT SERVICE BUS (Phase 2):
//   Phase 2:  sender.SendMessageAsync(new ServiceBusMessage(json))
//   Post 25:  daprClient.PublishEventAsync("order-pubsub", "orders", order)
//   The Dapr call is simpler. The component YAML handles authentication,
//   connection pooling, and broker-specific configuration.
app.MapPost("/orders/create-dapr", async (
    Dapr.Client.DaprClient daprClient,
    ILogger<Program> logger,
    HttpContext ctx) =>
{
    using var activity = Extensions.ActivitySource.StartActivity("CreateOrderDapr");
    var orderId  = $"order-{Random.Shared.Next(10000, 99999)}";
    var tenantId = ctx.User?.FindFirst("tenant_id")?.Value ?? "tenant-01";

    var order = new Contracts.OrderCreatedEvent
    {
        OrderId   = orderId,
        TenantId  = tenantId,
        CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
    };

    activity?.SetTag(TelemetryConstants.AttrOrderId, orderId);
    activity?.SetTag(TelemetryConstants.AttrTenantId, tenantId);

    // POST 25: PublishEventAsync — the Dapr pub/sub publish call.
    // Parameters: component name, topic name, event object.
    // The sidecar serialises the event to JSON and publishes to the broker.
    // Subscribers receive via the [Topic("order-pubsub", "orders")] attribute.
    await daprClient.PublishEventAsync(
        pubsubName: "order-pubsub",
        topicName:  "orders",
        data:        order);

    logger.LogInformation(
        "Order published via Dapr pub/sub. {OrderId} {TenantId}",
        orderId, tenantId);

    return Results.Accepted($"/orders/dapr/{orderId}",
        new { OrderId = orderId, TenantId = tenantId, Method = "Dapr pub/sub" });
}).RequireAuthorization("RequireEnabledTenant");

// POST 9: Deliberately poison message endpoint.
// Sends a message with OrderId prefixed "POISON_" — the OrderProcessor
// handler recognises this prefix and throws InvalidOperationException
// on every delivery attempt, exhausting MaxDeliveryCount: 3 and landing
// the message in the DLQ. Used in the Explore it section.
app.MapPost("/orders/create-poison", async (
    ILogger<Program> logger,
    HttpContext httpContext) =>
{
    var sender = httpContext.RequestServices.GetService<ServiceBusSender>();
    var poisonId = $"POISON_{Guid.NewGuid():N}";
    var tenantId = "tenant-01";

    var poisonEvent = new OrderCreatedEvent
    {
        OrderId    = poisonId,
        TenantId   = tenantId,
        CreatedAt  = DateTimeOffset.UtcNow.ToString("O"),
        AmountCents= 0,
        Currency   = "USD",
    };

    if (sender is not null)
    {
        var body    = BinaryData.FromObjectAsJson(poisonEvent,
                          OrderCreatedEventJsonContext.Default.Options);
        var message = new ServiceBusMessage(body)
        {
            ContentType   = "application/json",
            Subject       = "OrderCreated",
            MessageId     = poisonId,   // prefixed — triggers failure in handler
        };
        await sender.SendMessageAsync(message);

        logger.LogWarning(
            "Poison message sent deliberately. {OrderId} — will dead-letter after {MaxDeliveryCount} attempts",
            poisonId, 3);
    }

    return Results.Accepted($"/orders/dlq/{poisonId}",
        new { PoisonOrderId = poisonId, Status = "sent — will dead-letter" });
});

// POST 10: Transient failure test endpoint.
// OrderProcessor has TRANSIENT_ prefix detection that fails the first 2 Polly
// attempts and succeeds on the 3rd. Use this to demonstrate that DeliveryCount
// stays at 1 even after 2 failures — because Polly retried inside the handler.
app.MapPost("/orders/create-transient", async (
    ILogger<Program> logger, HttpContext httpContext) =>
{
    var sender = httpContext.RequestServices.GetService<ServiceBusSender>();
    var transientId = $"TRANSIENT_{Guid.NewGuid():N}";
    var body = BinaryData.FromObjectAsJson(
        new OrderCreatedEvent { OrderId = transientId, TenantId = "tenant-01",
                                CreatedAt = DateTimeOffset.UtcNow.ToString("O") },
        OrderCreatedEventJsonContext.Default.Options);
    if (sender is not null)
    {
        await sender.SendMessageAsync(new ServiceBusMessage(body)
            { MessageId = transientId, Subject = "OrderCreated" });
        logger.LogWarning(
            "Transient-failure test message sent. {OrderId} — will retry twice then succeed",
            transientId);
    }
    return Results.Accepted($"/orders/polly/{transientId}",
        new { TransientOrderId = transientId, Status = "sent — Polly will retry" });
});

app.Run();

record UserServiceStatus(string Status, string Version);
