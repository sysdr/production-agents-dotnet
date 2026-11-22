using DlqTriage;
using Microsoft.Extensions.AI;
using OllamaSharp;

// Optional: runs against a local model. Needs Ollama running and the model pulled:
//   ollama pull llama3.2
var model = args.FirstOrDefault() ?? "llama3.2";
IChatClient chat = new OllamaApiClient(new Uri("http://localhost:11434"), model);

var triager = new DlqTriager(chat);
var result = await triager.TriageAsync(new DeadLetterInfo(
    MessageId: "ord-1042",
    Reason: "MaxDeliveryCountExceeded",
    Description: "Message could not be consumed after 10 delivery attempts.",
    Body: """{"orderId":1042,"channel":"Gift","amountCents":1999}"""));

Console.WriteLine($"{result.Category} | replay: {result.SafeToReplay} | {result.Summary}");
