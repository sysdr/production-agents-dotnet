# Case study: healthy messages in the dead-letter queue

Reproduces how a large `PrefetchCount` dead-letters messages your handler never failed.

Prerequisite: the Lesson 0.1 stack running (it provides the Service Bus emulator, queue `orders-fallback`: lock 30s, max delivery 3).

```bash
dotnet run -- 12 220   # prefetch 12, run 220s
dotnet run -- 0 110    # prefetch 0, run 110s
```

Measured on the emulator (handler takes 8s, one at a time):

| PrefetchCount | Completed | Lock lost | Dead-lettered |
|---|---|---|---|
| 12 | 4 | 23 | 8 (`MaxDeliveryCountExceeded`) |
| 0 | 12 | 0 | 0 |

Safe ceiling: `PrefetchCount <= MaxConcurrentCalls x (LockDuration / p99 handler time)`.
Here: 1 x (30s / 8s) = 3. The run completed exactly the first 4 messages (1 in the handler + 3 buffered).
