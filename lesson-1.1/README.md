# Lesson 1.1: Put the model behind IChatClient and test it without calling a model

You build a small **dead-letter triager**: it reads one dead-lettered Service Bus message and returns a typed
`TriageResult` (category, safe to replay, one-line summary). The model proposes; your code decides.

## Run the tests (no model, no API key, no network)

```
dotnet test Lesson11.slnx
```

Success criteria: `Passed: 6`. The tests prove that:

1. a valid model reply becomes a typed `TriageResult`
2. a chatty, non-JSON reply falls back to `Unknown` instead of crashing the consumer
3. the model can't mark bad data as safe to replay
4. a transient failure can be replayed
5. the request asks the model for JSON with a schema
6. long message bodies are cut before they reach the model

## Optional: run against a local model

Needs [Ollama](https://ollama.com) running locally:

```
ollama pull llama3.2
dotnet run --project src/DlqTriage.Cli
```

The CLI compiles in CI; it is not run there, because CI has no model. Any provider that ships an `IChatClient`
works the same way: only the line that creates `chat` changes.

## Exercise

Add a fifth category, `Duplicate`, for messages your idempotency check already processed. Write the test first.
