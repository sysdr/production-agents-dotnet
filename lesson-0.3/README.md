# Lesson 0.3: .NET 11 and C# 15 for backend engineers

One concept: **model a message handler's decision as a C# 15 union**, so the compiler proves every outcome is settled, and send it over the wire safely.

## Run it

```bash
dotnet test Lesson03.slnx    # needs the .NET 11 SDK (11.0.100+)
```

No local .NET 11? Use the SDK image:

```bash
docker run --rm -v "$PWD":/w -w /w mcr.microsoft.com/dotnet/sdk:11.0 dotnet test Lesson03.slnx
```

Expected: `Passed! - Failed: 0, Passed: 6`.

## What's here

| File | Point |
|---|---|
| `src/OrderSettlement/HandleResult.cs` | `union HandleResult(Processed, Retry, DeadLetter)` with a `kind` field on each case |
| `src/OrderSettlement/Settlement.cs` | Exhaustive `switch`; `CS8509` is an error in `Directory.Build.props` |
| `src/OrderSettlement/HandleResultClassifier.cs` | `JsonTypeClassifierFactory<HandleResult>` reads `kind`, so JSON round-trips |
| `tests/...` | Round-trip per case, unknown kind fails, and proof that a union without a classifier is ambiguous |
| `samples/net10-polyfill` | Unsupported: unions compiled on `net10.0` with two polyfilled types, in-process only |

## Exercise

Add `public sealed record Defer(string OrderId, DateTimeOffset Until);` to the union. The build fails in `Settlement.Decide` (CS8509). Handle it with `SettleVerb.Defer`, teach the classifier `"defer"`, and add a round-trip test.

Measured facts this lesson relies on (SDK 11.0.100):
- A union on `net10.0` fails with CS0518 (`IUnion` missing) unless polyfilled.
- `JsonSerializer.Serialize` writes a union as its case value with no type tag.
- Deserializing two object-shaped cases without a classifier throws `JsonException` ("ambiguous").
