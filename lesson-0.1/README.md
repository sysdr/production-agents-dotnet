# Lesson 0.1: .NET 8 is out of support, upgrade to .NET 10

**Course:** Production AI Agents on .NET (Module 0, Lesson 1)
**Starting point:** the Course 1 final system (post 45, .NET 8 + Aspire 9)
**End state:** the same system on .NET 10 LTS + Aspire 13, with the full stack booting in Docker and every integration test passing.

.NET 8 and .NET 9 reached end of support on November 10, 2026. This lesson upgrades a real multi-service system rather than a toy app. Every change below happened while this code was being upgraded and verified.

## What you'll be able to do

- Move a multi-project solution to .NET 10 with **central package management** in under an hour.
- Read and fix the breaking changes that a framework upgrade actually surfaces: compile errors, obsolete APIs, runtime-only failures, and emulator drift.
- Prove the upgrade with evidence: a clean build, green integration tests, and an order flowing end to end.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.100 or later | `dotnet --version` |
| Docker Desktop / Docker Engine | 24+ | `docker info` |
| Dapr CLI | 1.16+ | `dapr --version` |
| Aspire CLI (optional) | 13.x | `aspire --version` |

Plan for about 6 GB of free RAM, since five emulator containers run alongside four services.

## Run it

```bash
dotnet build ProductionAgents.slnx          # 0 errors
dotnet test  ProductionAgents.slnx          # 8/8 pass (needs Docker; uses Testcontainers)
dotnet run --project src/AppHost            # or: aspire run
```

The AppHost starts SQL Server 2022, the Service Bus emulator, the Event Hubs emulator, Azurite and the OTel collector in Docker. It then starts UserService, OrderService, OrderProcessor and OrderEventProcessor with Dapr sidecars. Open the dashboard URL that it prints (http://localhost:15888).

## Verify end to end

With the AppHost running, open a second terminal and run:

```bash
./verify-lesson-0.1.sh
```

Expected output:

```
UserService  /health -> 200
OrderService /health -> 200
POST /orders/create -> 201 {"orderId":"order-81733",...}
Blob written: 2026/11/tenant-01/order-81733.json
PASS
```

The blob shows that the order went **API, then Service Bus, then OrderProcessor, then Blob Storage**, all on .NET 10.

## What changed, and why

### 1. Build plumbing (the part you'll reuse on every repo)

| File | Purpose |
|---|---|
| `global.json` | Pins SDK 10.0.100 with `rollForward: latestFeature`. |
| `Directory.Build.props` | One place for `net10.0`, nullable, implicit usings, and NuGet audit. |
| `Directory.Packages.props` | Central package management. Every version lives in one file, and csproj files carry no `Version=` attributes. |
| `ProductionAgents.slnx` | The new XML solution format (`dotnet new sln --format slnx`). |

### 2. Aspire 9 to Aspire 13

- `<Project Sdk="Aspire.AppHost.Sdk/13.6.5">` replaces the separate `<Sdk>` element, `IsAspireHost` and the `Aspire.Hosting.AppHost` package. The SDK adds that package implicitly, and declaring a central version for it is **error NU1009**.
- `Aspire.Hosting.Dapr` is deprecated. It's replaced by **`CommunityToolkit.Aspire.Hosting.Dapr`**, and `AddDapr` / `WithDaprSidecar` keep the same API.

### 3. Packages removed because they ship in the framework

`System.Text.Json`, `Microsoft.Extensions.Caching.Memory` and `Microsoft.Extensions.Diagnostics.HealthChecks` are part of the .NET 10 shared framework. NuGet now flags these with **warning NU1510**, so delete them.

### 4. Breaking changes found by the compiler

| Where | Symptom | Fix |
|---|---|---|
| `tests/LoadTests` (NBomber 5 to 6) | `ReportFormat` not found, `Response.Ok(int)` / `Fail(error:)` don't compile | `using NBomber.Contracts.Stats;`, string status codes, `message:` parameter |
| `tests/LoadTests/Program.cs` | CS0136 `args` redeclared | Top-level programs already have `args` |
| `tests/LoadTests/LoadTests.csproj` | Project won't load | `--` inside an XML comment is illegal. MSBuild in .NET 10 enforces this. |
| `OrderEventProcessor` | CS0618 `EventData.Offset` obsolete | Use `OffsetString`. Event Hubs no longer guarantees numeric offsets. |

### 5. Breaking change found only by the tests

`OrderBlobStore` probed `options.GetConverter(typeof(ProcessedOrderRecord))` and expected null so it could fall back to reflection. With a source-generated `JsonSerializerContext`, .NET 10 **throws `NotSupportedException`** instead. 3 of 8 integration tests failed. The fix is a dedicated `ProcessedOrderJsonContext`, which is also trim and AOT safe.

> Lesson: a green build is not an upgrade. Only the tests caught this one.

### 6. Breaking changes found only by booting the stack

The emulator images were pulled as `:latest`, and their rules changed since Course 1:

| Component | Error | Fix |
|---|---|---|
| Azure SQL Edge | Retired Sep 30, 2025; segfaults on current kernels | `mcr.microsoft.com/mssql/server:2022-latest` (the resource keeps the name `sqledge`) |
| Service Bus / Event Hubs emulators | `Login failed for user 'sa'` | Pass `MSSQL_SA_PASSWORD` to the emulators |
| Event Hubs emulator | Namespace must be `emulatorns1`; `$Default` can't be declared | Renamed the namespace and removed the group (it's created for you) |
| Event Hubs emulator | `MetadataStore / BlobStore Unhealthy` | Now stores metadata in Azurite: `BLOB_SERVER` / `METADATA_SERVER = azurite` |
| Both emulators | `Logging config cannot be null` | `LoggingConfig` / `Logging: { "Type": "File" }` |
| Service Bus emulator | `Max DefaultMessageTimeToLive supported 1h`, `Max Duplicate detection window supported 5m` | `PT1H`, `PT5M` |
| Service Bus emulator | `NullReferenceException` on config | Every queue and subscription property spelled out; rules use `"FilterType": "Sql"` |
| AppHost | Messaging silently disabled on a fresh clone | Post 45 relied on user secrets. The AppHost now passes `ConnectionStrings__servicebus` / `__eventhubs`. |

> Lesson: pin your images and your connection wiring. "Works on my machine" was hiding in user secrets.

### 7. CI

Both workflows now use `dotnet-version: '10.0.x'`.

## Exercise

1. The emulator images are pinned in `src/AppHost/Program.cs`. Check for a newer tag of each, bump one, re-run the stack, and note what changed.
2. Run `dotnet list package --outdated` and `dotnet list package --vulnerable`, and explain each result.
3. Stretch: target `net11.0` (STS) in a branch and record what else breaks.

## Next lesson

**Lesson 0.2: Aspire as your local platform** runs this same stack from one AppHost, then Lesson 0.3 moves the settlement logic to C# 15 unions.
