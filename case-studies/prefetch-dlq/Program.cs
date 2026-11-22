using Azure.Messaging.ServiceBus;
const string cs = "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
await using var client = new ServiceBusClient(cs);
var queue = "orders-fallback"; // lock 30s, max delivery 3, no dup detection
var sender = client.CreateSender(queue);
for (int i = 0; i < 12; i++) await sender.SendMessageAsync(new ServiceBusMessage($"order-{i}") { MessageId = $"order-{i}" });
Console.WriteLine("sent 12");
var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions {
    PrefetchCount = int.Parse(args[0]), MaxConcurrentCalls = 1, AutoCompleteMessages = false });
int ok = 0, lost = 0;
processor.ProcessMessageAsync += async a => {
    await Task.Delay(TimeSpan.FromSeconds(8));   // "slow but fine" handler
    try { await a.CompleteMessageAsync(a.Message); ok++; Console.WriteLine($"{DateTime.Now:T} completed {a.Message.MessageId} delivery={a.Message.DeliveryCount}"); }
    catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.MessageLockLost) { lost++; Console.WriteLine($"{DateTime.Now:T} LOCK LOST {a.Message.MessageId} delivery={a.Message.DeliveryCount}"); }
};
processor.ProcessErrorAsync += e => { Console.WriteLine("err " + e.Exception.Message.Split('.')[0]); return Task.CompletedTask; };
await processor.StartProcessingAsync();
await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[1])));
await processor.StopProcessingAsync();
var dlq = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
var dead = await dlq.ReceiveMessagesAsync(50, TimeSpan.FromSeconds(5));
foreach (var m in dead) await dlq.CompleteMessageAsync(m);
Console.WriteLine($"RESULT prefetch={args[0]} completed={ok} lockLost={lost} deadLettered={dead.Count} reasons={string.Join(",", dead.Select(d => d.DeadLetterReason).Distinct())}");
