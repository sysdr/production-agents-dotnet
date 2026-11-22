// src/AppHost/Program.cs
// POST 22: .NET Aspire as Infrastructure-as-Code
//
// This file is the source of truth for the entire system topology.
// Every service, container, emulator, and connection is declared here.
//
// WHY THIS IS IaC:
//   - Type-safe: renamed resource names fail at compile time
//   - Diffable: topology changes appear in git diffs like any code change
//   - Self-documenting: WithReference() makes service dependencies explicit
//   - Portable: the same file generates an Aspire manifest for ACA/AKS deployment
//
// ASPIRE MANIFEST:
//   dotnet run --project src/AppHost --publisher manifest --output-path aspire-manifest.json
//   The manifest JSON is the input for Azure Container Apps (azd) or Aspirate (Helm/K8s).
//   Post 35 covers Bicep IaC; the manifest bridges Aspire and Bicep.
//
// docker-compose.yml COMPARISON:
//   docker-compose.yml  │  AppHost/Program.cs
//   ───────────────────────────────────────────────────────────────
//   services:           │  builder.AddProject / AddContainer
//   depends_on:         │  WaitFor(resource)
//   environment:        │  WithEnvironment() / WithReference()
//   networks:           │  WithReference() (Aspire injects env vars)
//   healthcheck:        │  WithHealthCheck() / resource.WaitForCompletion
//   ports:              │  WithEndpoint(containerPort, hostPort)
//   volumes:            │  WithBindMount(hostPath, containerPath)
//
// CUMULATIVE FILE: each post adds resources. By Post 45 this
// orchestrates the entire 22-component system.

var builder = DistributedApplication.CreateBuilder(args);

// Resolve Dapr CLI when it is installed under %USERPROFILE%\.dapr\bin but not on PATH.
var daprCli = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".dapr", "bin", "dapr.exe");
builder.AddDapr(options =>
{
    if (File.Exists(daprCli))
        options.DaprPath = daprCli;
});

// ── JWT signing key (shared secret between UserService and OrderService) ───
//
// POST 22: The JWT signing key from appsettings.Development.json is injected
// as an environment variable by Aspire. Both services read the same value.
// In production: replace with a Key Vault secret reference —
//   var kv = builder.AddAzureKeyVault("keyvault");
//   userService.WithReference(kv);
//   orderService.WithReference(kv);
// The Jwt:SigningKey then comes from Key Vault instead of AppHost.
//
// IMPORTANT: This is for local development only.
// Do not hardcode secrets in AppHost/Program.cs in a production repository.
// Use builder.Configuration["Jwt:SigningKey"] so the value comes from
// environment variables or user-secrets rather than source code.
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? "hyperscale-dev-signing-key-32chars!!";

// ── Docker emulators (start first — services depend on them) ───────────────
//
// POST 22: These containers are declared first so WaitFor() dependency
// ordering is available. Aspire starts all resources in parallel by default;
// WaitFor() creates explicit startup sequencing for services that need
// emulators to be healthy before they start.
//
// Note: The Service Bus and Event Hubs emulators require SQL Edge to initialise
// before they are usable. In docker-compose.yml this is handled by the
// "depends_on: sqledge" stanza. In Aspire, WaitFor() serves the same role.

// LESSON 0.1: Azure SQL Edge was retired on Sep 30, 2025. The emulators now use
// SQL Server 2022 (Linux). The resource keeps the name "sqledge" so every
// SQL_SERVER=sqledge reference and WaitFor(sqledge) still works unchanged.
// Images are pinned: ":latest" emulator images changed their config rules
// under us (see the lesson notes), so upgrades are now deliberate.
const string sqlPassword = "YourStrong!Passw0rd";

var sqledge = builder.AddContainer("sqledge",
        "mcr.microsoft.com/mssql/server",
        "2022-latest")
    .WithEndpoint(port: 1433, targetPort: 1433, name: "sql")
    .WithEnvironment("ACCEPT_EULA", "Y")
    .WithEnvironment("MSSQL_SA_PASSWORD", sqlPassword);

var serviceBusEmulator = builder.AddContainer("servicebus-emulator",
        "mcr.microsoft.com/azure-messaging/servicebus-emulator",
        "2.0.1") // pinned: the tag Aspire 17 ships with
    .WithEndpoint(port: 5672, targetPort: 5672, name: "amqp")
    .WithBindMount("../../config/servicebus-config.json",
        "/ServiceBus_Emulator/ConfigFiles/Config.json", isReadOnly: true)
    .WithEnvironment("ACCEPT_EULA", "Y")
    .WithEnvironment("SQL_SERVER", "sqledge")
    // LESSON 0.1: emulators must log in with the same SA password as SQL Server.
    .WithEnvironment("MSSQL_SA_PASSWORD", sqlPassword)
    // POST 22: WaitFor() — service bus emulator needs SQL Edge to be running.
    // Aspire health-probes sqledge and does not start servicebus-emulator until healthy.
    .WaitFor(sqledge);

var azurite = builder.AddContainer("azurite",
        "mcr.microsoft.com/azure-storage/azurite",
        "3.37.0") // pinned
    .WithArgs("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
    // isProxied: false — bind host 10000-10002 so UseDevelopmentStorage=true works
    .WithEndpoint(port: 10000, targetPort: 10000, name: "blob", isProxied: false)
    .WithEndpoint(port: 10001, targetPort: 10001, name: "queue", isProxied: false)
    .WithEndpoint(port: 10002, targetPort: 10002, name: "table", isProxied: false);

var eventHubsEmulator = builder.AddContainer("eventhubs-emulator",
        "mcr.microsoft.com/azure-messaging/eventhubs-emulator",
        "2.2.1") // pinned: the tag Aspire 17 ships with
    .WithEndpoint(port: 5673, targetPort: 5672, scheme: "tcp", name: "amqp-eh")
    .WithBindMount("../../config/eventhubs-config.json",
        "/Eventhubs_Emulator/ConfigFiles/Config.json", isReadOnly: true)
    .WithEnvironment("ACCEPT_EULA", "Y")
    // LESSON 0.1: the current Event Hubs emulator keeps metadata and checkpoints
    // in Azurite (not SQL). Point it at the azurite resource and wait for it.
    .WithEnvironment("BLOB_SERVER", "azurite")
    .WithEnvironment("METADATA_SERVER", "azurite")
    .WaitFor(azurite);


const string azuriteConn =
    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

// LESSON 0.1: emulator connection strings. Post 45 relied on per-machine user
// secrets, so a fresh clone silently ran with messaging disabled. The AppHost
// now hands the emulator endpoints to every service that needs them.
// The emulators accept only the fixed development key "SAS_KEY_VALUE".
const string serviceBusConn =
    "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
const string eventHubsConn =
    "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

// ── OTel Collector ────────────────────────────────────────────────────────
var otelCollector = builder.AddContainer("otel-collector",
        "otel/opentelemetry-collector-contrib",
        "0.102.0")
    .WithBindMount("../../config/otel-collector-config.yaml",
        "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
    .WithEndpoint(port: 4317, targetPort: 4317, name: "grpc",
        scheme: "http", isExternal: false)
    .WithEndpoint(port: 55679, targetPort: 55679, name: "zpages",
        scheme: "http", isExternal: true);

// ── .NET services ─────────────────────────────────────────────────────────
//
// POST 22: WithEnvironment() and WaitFor() make the dependency graph explicit.
//
// WithEnvironment("Jwt__SigningKey", ...) — double-underscore notation maps to
// nested JSON: "Jwt:SigningKey" in appsettings.json. This is the standard
// ASP.NET Core environment variable configuration convention.
// The same convention applies to all nested configuration sections:
//   "Jwt__Issuer"    → appsettings "Jwt": { "Issuer": ... }
//   "AzureAd__TenantId" → appsettings "AzureAd": { "TenantId": ... }
//
// WithReference(service) — injects the service's base URL as an environment variable.
// Example: WithReference(userService) on orderService sets
//   services__user-service__http__0 = http://localhost:{userServicePort}
// The Aspire Service Discovery middleware resolves "user-service" → the injected URL.

var userService = builder.AddProject<Projects.UserService>("user-service")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("Jwt__Issuer",    "hyperscale-local")
    .WithEnvironment("Jwt__Audience",  "order-service")
    .WaitFor(azurite)
    // POST 24: WithDaprSidecar() starts the Dapr daprd process alongside UserService.
    // Dapr app-id: "user-service" — used by OrderService to invoke UserService endpoints.
    .WithDaprSidecar();

var orderService = builder.AddProject<Projects.OrderService>("order-service")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("Jwt__Issuer",    "hyperscale-local")
    .WithEnvironment("Jwt__Audience",  "order-service")
    .WithEnvironment("ConnectionStrings__azurite", azuriteConn)
    .WithEnvironment("ConnectionStrings__servicebus", serviceBusConn)
    .WithEnvironment("ConnectionStrings__eventhubs", eventHubsConn)
    // POST 22: OrderService depends on all emulators. WaitFor() ensures they
    // are healthy before OrderService starts processing requests.
    // Without this, OrderService starts and immediately fails its first health
    // check on the Service Bus connection (emulator not yet ready after 35s).
    .WaitFor(serviceBusEmulator)
    .WaitFor(eventHubsEmulator)
    .WaitFor(azurite)
    .WithReference(userService)
    // POST 24: WithDaprSidecar() on OrderService.
    // OrderService uses DaprClient to invoke UserService /users/status.
    .WithDaprSidecar();

var orderProcessor = builder.AddProject<Projects.OrderProcessor>("order-processor")
    .WithEnvironment("ConnectionStrings__azurite", azuriteConn)
    .WithEnvironment("ConnectionStrings__servicebus", serviceBusConn)
    .WaitFor(serviceBusEmulator)
    .WaitFor(azurite);

var orderEventProcessor = builder.AddProject<Projects.OrderEventProcessor>("order-event-processor")
    .WithEnvironment("ConnectionStrings__azurite", azuriteConn)
    .WithEnvironment("ConnectionStrings__eventhubs", eventHubsConn)
    .WaitFor(eventHubsEmulator)
    .WaitFor(azurite);

// ── UserService (already declared above) ─────────────────────────────────
// (userService and orderService declared before the emulators for reference;
// WaitFor() handles the actual startup order, not the code order)

builder.Build().Run();
